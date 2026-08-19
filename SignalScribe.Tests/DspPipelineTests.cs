using Microsoft.Extensions.Logging.Abstractions;
using SignalScribe.Capture.Dsp;
using SignalScribe.Contracts;
using SignalScribe.Enums;
using Xunit;

namespace SignalScribe.Tests;

public class DspPipelineTests
{
    private const double Fs = 3_200_000;

    private const double Spacing = 12_500;

    private const long CenterHz = 146_000_000;

    [Fact]
    public void ChannelizerRoutesToneToItsBin()
    {
        var channelizer = new PolyphaseChannelizer(Fs, Spacing);
        var sink = new PowerSink(channelizer.ChannelCount);

        // Tone exactly on bin +32 (center + 400 kHz).
        var toneHz = 32 * Spacing;
        var iq = MakeTone(toneHz, seconds: 0.05);
        channelizer.Process(iq, sink);

        var powers = sink.AveragePowerDb();
        Assert.InRange(powers[32], -8, -4); // 0.5-amplitude tone = -6 dBFS at unity filter gain
        Assert.True(powers[32] - powers[30] > 40, $"two bins away should be >40 dB down (got {powers[32] - powers[30]:F1})");
        Assert.True(powers[32] - powers[200] > 60, "far bins should be deeply attenuated");
    }

    [Fact]
    public void DemodRecoversFmTone()
    {
        // FM: 1 kHz audio tone at 2.5 kHz deviation, channel rate 25 kHz.
        var demod = new NbfmDemodulator(25_000);
        var iq = new float[25_000 * 2]; // 1 second
        double phase = 0;
        for (var n = 0; n < 25_000; n++)
        {
            var audio = Math.Sin(2 * Math.PI * 1000 * n / 25_000.0);
            phase += 2 * Math.PI * (2500 * audio) / 25_000;
            iq[2 * n] = (float)Math.Cos(phase);
            iq[2 * n + 1] = (float)Math.Sin(phase);
        }

        var pcm = new float[20_000];
        var written = demod.Process(iq, pcm);
        Assert.InRange(written, 15_000, 16_100); // ~1 s at 16 kHz

        // The recovered audio should be dominated by 1 kHz: check zero-crossing rate ≈ 2000/s.
        var crossings = 0;
        for (var i = 16_000 / 4; i < written - 1; i++) // skip de-emphasis/DC settle
        {
            if ((pcm[i] >= 0) != (pcm[i + 1] >= 0))
            {
                crossings++;
            }
        }

        var seconds = (written - written / 4) / 16_000.0;
        var rate = crossings / seconds;
        Assert.InRange(rate, 1800, 2200);
    }

    [Fact]
    public void DemodTracksCarrierOffset()
    {
        var demod = new NbfmDemodulator(25_000);
        var iq = MakeToneAtRate(1_500, 25_000, seconds: 0.5); // unmodulated carrier at +1.5 kHz offset
        var pcm = new float[10_000];
        demod.Process(iq, pcm);
        Assert.InRange(demod.CarrierOffsetHz, 1_300, 1_700);
    }

    [Fact]
    public void VoiceGatePassesSpeechLikeRejectsToneAndNoise()
    {
        // Speech-like: spectrally broad (formant-ish tone stack + noise), syllabically modulated at 4 Hz.
        var speechish = new AudioAnalyzer();
        speechish.Feed(MakeAudio(16_000 * 2, SpeechLikeSample));
        Assert.True(speechish.Finish().VoiceLike, "syllabic modulated speech-band audio should pass");

        // Steady pure tone: heterodyne/carrier — must fail (sustained tone).
        var tone = new AudioAnalyzer();
        tone.Feed(MakeAudio(16_000 * 2, n => 0.5f * MathF.Sin(2 * MathF.PI * 1000 * n / 16_000f)));
        var toneVerdict = tone.Finish();
        Assert.False(toneVerdict.VoiceLike, "steady tone should fail the voice gate");
        Assert.True(toneVerdict.SustainedTone, "steady tone should be flagged sustained (double)");

        // White noise: no syllabic structure — must fail.
        var rng = new Random(7);
        var noise = new AudioAnalyzer();
        noise.Feed(MakeAudio(16_000 * 2, _ => (float)(rng.NextDouble() * 0.8 - 0.4)));
        Assert.False(noise.Finish().VoiceLike, "white noise should fail the voice gate");
    }

    /// <summary>
    /// Observed on 147.180: someone talking over a steady background tone. The tone is audible in
    /// the gaps between words, so every pause reads as a pure tone and the gate threw the whole
    /// transmission away — 13 s of speech scoring 864 ms voiced and 0.15 speech ratio, because the
    /// tone outweighed the talking in the denominator of every frame.
    /// </summary>
    [Theory]
    [InlineData(120, 0.45)]    // hum or leaked CTCSS below the band — de-emphasis makes these loud
    [InlineData(3_400, 0.12)]  // link or data tone above it, as it survives 750us de-emphasis
    public void SpeechOverABackgroundToneStillCountsAsVoice(double toneHz, double toneAmplitude)
    {
        var a = new AudioAnalyzer();
        a.Feed(MakeAudio(16_000 * 3, n =>
        {
            // Someone talking with a pause in the middle, over a tone that never stops. The pause is
            // the whole point: that is where the tone is alone and reads as a sustained pure tone.
            var t = n / 16_000.0;
            var talking = t is < 1.2 or > 1.9;
            var tone = (float)toneAmplitude * MathF.Sin(2 * MathF.PI * (float)toneHz * n / 16_000f);
            return (talking ? SpeechLikeSample(n) : 0f) + tone;
        }));

        var v = a.Finish();
        Assert.True(v.SustainedTone, "the tone is real and should still be reported");
        Assert.True(v.VoiceLike, $"speech over a {toneHz} Hz tone should still pass, got {v}");
    }

    /// <summary>
    /// Observed on 146.925: a transmission with an audible hum came through flagged [double], so it
    /// was never transcribed. A double is two carriers beating into a whistle in the voice band —
    /// hum sits below it and is something the speech rides on, not a second station.
    /// </summary>
    [Fact]
    public void HumDoesNotMakeATransmissionADouble()
    {
        var withHum = new AudioAnalyzer();
        withHum.Feed(MakeAudio(16_000 * 3, n =>
        {
            var t = n / 16_000.0;
            var talking = t is < 1.2 or > 1.9;
            return (talking ? SpeechLikeSample(n) : 0f) + 0.45f * MathF.Sin(2 * MathF.PI * 120 * n / 16_000f);
        }));

        var hum = withHum.Finish();
        Assert.True(hum.SustainedTone, "the hum is a steady tone and should still be reported as one");
        Assert.False(hum.HeterodyneTone, "a 120 Hz hum is not two carriers beating");
        Assert.True(hum.VoiceLike);

        // A real double: a beat note in the voice band, running the whole way through.
        var doubled = new AudioAnalyzer();
        doubled.Feed(MakeAudio(16_000 * 3, n => 0.5f * MathF.Sin(2 * MathF.PI * 900 * n / 16_000f)));

        var beat = doubled.Finish();
        Assert.True(beat.HeterodyneTone, "a 900 Hz beat note is what a double sounds like");
        Assert.False(beat.VoiceLike);
    }

    /// <summary>A tone *inside* the voice band can be mistaken for speech, so it must still be refused.</summary>
    [Fact]
    public void AnInBandToneIsStillNotVoiceEvenWhenItIsInterrupted()
    {
        // Interrupted 1 kHz tone: syllable-rate on/off keying, in-band, but no speech under it.
        var a = new AudioAnalyzer();
        a.Feed(MakeAudio(16_000 * 3, n =>
        {
            var gate = Math.Sin(2 * Math.PI * 4 * n / 16_000.0) > 0 ? 1f : 0f;
            return gate * 0.5f * MathF.Sin(2 * MathF.PI * 1000 * n / 16_000f);
        }));

        Assert.False(a.Finish().VoiceLike, "an in-band tone must not pass as speech");
    }

    [Fact]
    public void EndToEndBurstProducesClipAndIngest()
    {
        var audioRoot = Path.Combine(Path.GetTempPath(), $"ss-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(audioRoot);
        try
        {
            var channelizer = new PolyphaseChannelizer(Fs, Spacing);
            var posted = new List<TransmissionIngest>();
            var bank = new ChannelBank(
                channelizer.ChannelCount, channelizer.OutputSampleRate, CenterHz,
                bin => channelizer.BinFrequencyHz(bin, CenterHz),
                openDb: 8, closeDb: 5, hangMs: 300,
                audioRoot, new DateTime(2026, 8, 16, 0, 0, 0, DateTimeKind.Utc),
                f => f, // known frequency: bypass voice gate
                posted.Add,
                NullLogger.Instance);

            // 0.4 s noise, 1.6 s FM burst on bin +32, 0.8 s noise (hang + close).
            var sink = new ForwardSink(bank);
            channelizer.Process(MakeNoise(0.4), sink);
            channelizer.Process(MakeFmBurst(32 * Spacing, seconds: 1.6), sink);
            channelizer.Process(MakeNoise(0.8), sink);

            Assert.Single(posted);
            var tx = posted[0];
            Assert.Equal(CenterHz + (long)(32 * Spacing), tx.FrequencyHz);
            Assert.InRange((tx.EndUtc - tx.StartUtc).TotalSeconds, 1.2, 2.6);
            Assert.Contains(tx.Markers, m => m.Type == MarkerType.RfEdgeRise);
            Assert.Contains(tx.Markers, m => m.Type == MarkerType.RfEdgeFall);

            var clip = Path.Combine(audioRoot, tx.AudioPath);
            Assert.True(File.Exists(clip), "Opus clip should exist");
            Assert.True(new FileInfo(clip).Length > 2000, "clip should contain encoded audio");
        }
        finally
        {
            Directory.Delete(audioRoot, recursive: true);
        }
    }

    [Fact]
    public void VoiceGatedBurstFromUnknownFrequencyPostsThroughFullChain()
    {
        // Same as EndToEnd but with the voice gate ACTIVE (unknown frequency): the demodulated
        // audio of a speech-like FM burst must still read as voice after the full DSP chain.
        var audioRoot = Path.Combine(Path.GetTempPath(), $"ss-vg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(audioRoot);
        try
        {
            var channelizer = new PolyphaseChannelizer(Fs, Spacing);
            var posted = new List<TransmissionIngest>();
            var bank = new ChannelBank(
                channelizer.ChannelCount, channelizer.OutputSampleRate, CenterHz,
                bin => channelizer.BinFrequencyHz(bin, CenterHz),
                openDb: 8, closeDb: 5, hangMs: 300,
                audioRoot, new DateTime(2026, 8, 16, 0, 0, 0, DateTimeKind.Utc),
                _ => null, // UNKNOWN frequency — the voice gate decides
                posted.Add,
                NullLogger.Instance);

            var sink = new ForwardSink(bank);
            channelizer.Process(MakeNoise(0.4), sink);
            channelizer.Process(MakeFmBurst(32 * Spacing, seconds: 2.0), sink);
            channelizer.Process(MakeNoise(0.8), sink);

            Assert.True(posted.Count == 1,
                $"speech-like burst should pass the voice gate on an unknown frequency (posted {posted.Count})");
        }
        finally
        {
            Directory.Delete(audioRoot, recursive: true);
        }
    }

    /// <summary>
    /// A known channel that sits off the analysis grid still gets its record-everything trust.
    ///
    /// The known set holds real channel frequencies while the bank works in bins, and most of a
    /// 5 kHz band plan sits off the 12.5 kHz grid — 147.180 lives 5 kHz above its 147.175 bin.
    /// An exact-match known lookup therefore missed every off-grid channel: measured on air, the
    /// voice gate was discarding real overs on 147.180 and 144.920 while both were known and
    /// enabled. Resolution must go through half a bin, and the posted frequency must be the
    /// channel's rather than the measurement, or clips whose offset never settles post under bin
    /// centres and spring phantom half-grid channels into existence (144.3875 collected 29 clips
    /// that belonged to 144.390).
    /// </summary>
    [Fact]
    public void OffGridKnownChannelKeepsANonVoiceClip()
    {
        var audioRoot = Path.Combine(Path.GetTempPath(), $"ss-offgrid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(audioRoot);
        try
        {
            // A steady unmodulated carrier 4 kHz above bin 32 — nothing about it is voice-like, so
            // what happens to it is decided entirely by whether the channel is trusted. The channel
            // deliberately sits off the 5 kHz snap grid (a 12.5 kHz-plan frequency, which real
            // channel lists do contain), so the measurement snaps to 5 kHz *away* from it and the
            // posted frequency proves which of the two the ingest actually used.
            var knownFrequency = CenterHz + (long)(32 * Spacing) + 4_000;
            var snapWouldSay = CenterHz + (long)(32 * Spacing) + 5_000;

            // Trap armed: on an unknown frequency the gate throws this away.
            var control = RunCarrierThrough(audioRoot, _ => null, out var controlDiscards);
            Assert.Empty(control);
            Assert.NotEmpty(controlDiscards);

            // Known off-grid channel: kept, and filed under the channel rather than the reading.
            long[] knownSet = [knownFrequency];
            var posted = RunCarrierThrough(
                audioRoot,
                f => KnownFrequencyResolver.Nearest(knownSet, f, (long)(Spacing / 2)),
                out var discarded);

            var tx = Assert.Single(posted);
            Assert.Equal(knownFrequency, tx.FrequencyHz);
            Assert.NotEqual(snapWouldSay, tx.FrequencyHz);
            Assert.DoesNotContain(discarded, d => Math.Abs(d.FrequencyHz - knownFrequency) <= (long)(Spacing / 2));
        }
        finally
        {
            Directory.Delete(audioRoot, recursive: true);
        }
    }

    private static List<TransmissionIngest> RunCarrierThrough(
        string audioRoot, Func<long, long?> resolveKnown, out List<DiscardIngest> discarded)
    {
        var channelizer = new PolyphaseChannelizer(Fs, Spacing);
        var posted = new List<TransmissionIngest>();
        var discards = new List<DiscardIngest>();
        var bank = new ChannelBank(
            channelizer.ChannelCount, channelizer.OutputSampleRate, CenterHz,
            bin => channelizer.BinFrequencyHz(bin, CenterHz),
            openDb: 8, closeDb: 5, hangMs: 300,
            audioRoot, DateTime.UtcNow,
            resolveKnown, posted.Add, NullLogger.Instance, postDiscard: discards.Add);
        var sink = new ForwardSink(bank);

        channelizer.Process(MakeNoise(0.4), sink);
        channelizer.Process(MakeTone((32 * Spacing) + 4_000, 1.5), sink);
        channelizer.Process(MakeNoise(0.8), sink);

        discarded = discards;
        return posted;
    }

    [Fact]
    public void ActiveGatesAreReportedForLiveStatus()
    {
        var audioRoot = Path.Combine(Path.GetTempPath(), $"ss-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(audioRoot);
        try
        {
            var channelizer = new PolyphaseChannelizer(Fs, Spacing);
            var bank = new ChannelBank(
                channelizer.ChannelCount, channelizer.OutputSampleRate, CenterHz,
                bin => channelizer.BinFrequencyHz(bin, CenterHz),
                openDb: 8, closeDb: 5, hangMs: 300,
                audioRoot, DateTime.UtcNow, f => f, _ => { }, NullLogger.Instance);
            var sink = new HopSink(bank);

            channelizer.Process(MakeNoise(0.4), sink);
            Assert.Empty(bank.ActiveGates(sink.LastHop));

            channelizer.Process(MakeFmBurst(32 * Spacing, seconds: 1.0), sink);

            // While the carrier is up the UI must be able to see what is being recorded.
            var gates = bank.ActiveGates(sink.LastHop);
            var gate = Assert.Single(gates);
            Assert.Equal(CenterHz + (long)(32 * Spacing), gate.FrequencyHz);
            Assert.True(gate.Seconds > 0.5, $"elapsed should track the open gate (got {gate.Seconds}s)");
            Assert.True(gate.Known);
        }
        finally
        {
            Directory.Delete(audioRoot, recursive: true);
        }
    }

    [Fact]
    public void NoiseAloneOpensNoGates()
    {
        var audioRoot = Path.Combine(Path.GetTempPath(), $"ss-noise-{Guid.NewGuid():N}");
        Directory.CreateDirectory(audioRoot);
        try
        {
            var channelizer = new PolyphaseChannelizer(Fs, Spacing);
            var posted = new List<TransmissionIngest>();
            var bank = new ChannelBank(
                channelizer.ChannelCount, channelizer.OutputSampleRate, CenterHz,
                bin => channelizer.BinFrequencyHz(bin, CenterHz),
                openDb: 8, closeDb: 5, hangMs: 300,
                audioRoot, DateTime.UtcNow, f => f, posted.Add, NullLogger.Instance);
            var sink = new ForwardSink(bank);
            for (var i = 0; i < 6; i++)
            {
                channelizer.Process(MakeNoise(0.4), sink);
            }

            Assert.True(posted.Count == 0, "noise-only opened: " + string.Join(",", posted.ConvertAll(p2 => (p2.FrequencyHz - CenterHz) / 12500)));
        }
        finally
        {
            Directory.Delete(audioRoot, recursive: true);
        }
    }

    [Fact]
    public void BandWideGainStepDoesNotOpenGates()
    {
        // Observed on air: the RSP's AGC steps gain for every channel at once. In absolute-level
        // squelch that lifts all channels above their floors, latching 30+ gates open on noise.
        var audioRoot = Path.Combine(Path.GetTempPath(), $"ss-agc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(audioRoot);
        try
        {
            var channelizer = new PolyphaseChannelizer(Fs, Spacing);
            var posted = new List<TransmissionIngest>();
            var bank = new ChannelBank(
                channelizer.ChannelCount, channelizer.OutputSampleRate, CenterHz,
                bin => channelizer.BinFrequencyHz(bin, CenterHz),
                openDb: 8, closeDb: 5, hangMs: 300,
                audioRoot, DateTime.UtcNow,
                f => f, posted.Add, NullLogger.Instance);
            var sink = new ForwardSink(bank);

            channelizer.Process(MakeNoise(0.5), sink);
            channelizer.Process(MakeNoise(0.5, amplitude: 0.08), sink); // +20 dB band-wide step
            channelizer.Process(MakeNoise(0.5), sink);                  // and back down

            Assert.Empty(posted);
            Assert.Equal(0, bank.OpenGateCount);
        }
        finally
        {
            Directory.Delete(audioRoot, recursive: true);
        }
    }

    /// <summary>
    /// Observed on air: 147.5625 MHz stayed gated open for ten unbroken minutes, emitting a 90 s
    /// noise clip every rollover (89 clips, 2.2 hours of audio, all discarded as "not voice").
    /// Two bugs compounded — the max-open valve measured from the *clip* start, so every rollover
    /// reset it and it never fired; and the noise floor is frozen while a gate is open, so once
    /// latched the channel could never fall back below its own close threshold.
    /// </summary>
    [Fact]
    public void PersistentCarrierIsForceClosedOnceAndDoesNotRelatch()
    {
        var audioRoot = Path.Combine(Path.GetTempPath(), $"ss-latch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(audioRoot);
        try
        {
            var channelizer = new PolyphaseChannelizer(Fs, Spacing);
            var posted = new List<TransmissionIngest>();
            var discarded = new List<DiscardIngest>();
            var bank = new ChannelBank(
                channelizer.ChannelCount, channelizer.OutputSampleRate, CenterHz,
                bin => channelizer.BinFrequencyHz(bin, CenterHz),
                openDb: 8, closeDb: 5, hangMs: 100,
                audioRoot, DateTime.UtcNow,
                _ => null, posted.Add, NullLogger.Instance,
                postDiscard: discarded.Add,
                // Compressed from 180/90 s so the scenario fits in a test — the logic is identical.
                maxOpenSeconds: 1.0, clipRolloverSeconds: 0.4);
            var sink = new ForwardSink(bank);

            channelizer.Process(MakeNoise(0.3), sink); // settle the floors

            // An unmodulated birdie that never goes away — a spur, a data carrier, a stuck tone.
            for (var i = 0; i < 10; i++)
            {
                channelizer.Process(MakeCarrierOnNoise(75_000, 0.5), sink);
            }

            bank.CloseAll(long.MaxValue / 2);

            // Five seconds of unbroken carrier must not become an endless chain of rollover clips.
            var clips = posted.Count + discarded.Count;
            Assert.True(clips <= 3, $"a persistent carrier produced {clips} clips — it re-latched");
            Assert.Equal(0, bank.OpenGateCount);
        }
        finally
        {
            Directory.Delete(audioRoot, recursive: true);
        }
    }

    /// <summary>
    /// Observed on air: a ~29 s burst opened twelve gates between 147.0 and 148.0 MHz at the same
    /// instant, every one within 1 dB of the others, every one later discarded as "not voice".
    /// The band-median reference misses it because the burst covers a fraction of the span.
    /// </summary>
    [Fact]
    public void PartialBandLevelEventOpensNoGates()
    {
        var audioRoot = Path.Combine(Path.GetTempPath(), $"ss-burst-{Guid.NewGuid():N}");
        Directory.CreateDirectory(audioRoot);
        try
        {
            var channelizer = new PolyphaseChannelizer(Fs, Spacing);
            var posted = new List<TransmissionIngest>();
            var discarded = new List<DiscardIngest>();
            var bank = new ChannelBank(
                channelizer.ChannelCount, channelizer.OutputSampleRate, CenterHz,
                bin => channelizer.BinFrequencyHz(bin, CenterHz),
                openDb: 8, closeDb: 5, hangMs: 300,
                audioRoot, DateTime.UtcNow,
                _ => null, posted.Add, NullLogger.Instance, postDiscard: discarded.Add);
            var sink = new ForwardSink(bank);

            channelizer.Process(MakeNoise(0.4), sink);                      // settle the floors
            channelizer.Process(MakeBandBurst(12, 0.6), sink);              // twelve channels lift together
            channelizer.Process(MakeNoise(0.4), sink);                      // and it passes

            Assert.Empty(posted);
            Assert.Empty(discarded);
            Assert.Equal(0, bank.OpenGateCount);
        }
        finally
        {
            Directory.Delete(audioRoot, recursive: true);
        }
    }

    /// <summary>
    /// A real transmission must still open a gate immediately after a level event has passed —
    /// the suppression raises floors, and the fast-down tracking has to give them back.
    /// </summary>
    [Fact]
    public void RealSignalStillOpensAfterALevelEvent()
    {
        var audioRoot = Path.Combine(Path.GetTempPath(), $"ss-after-{Guid.NewGuid():N}");
        Directory.CreateDirectory(audioRoot);
        try
        {
            var channelizer = new PolyphaseChannelizer(Fs, Spacing);
            var posted = new List<TransmissionIngest>();
            var bank = new ChannelBank(
                channelizer.ChannelCount, channelizer.OutputSampleRate, CenterHz,
                bin => channelizer.BinFrequencyHz(bin, CenterHz),
                openDb: 8, closeDb: 5, hangMs: 300,
                audioRoot, DateTime.UtcNow,
                f => f, posted.Add, NullLogger.Instance);
            var sink = new ForwardSink(bank);

            channelizer.Process(MakeNoise(0.4), sink);
            channelizer.Process(MakeBandBurst(12, 0.6), sink);
            channelizer.Process(MakeNoise(0.6), sink);        // floors recover
            channelizer.Process(MakeFmBurst(75_000, 1.5), sink);
            channelizer.Process(MakeNoise(0.6), sink);
            bank.CloseAll(long.MaxValue / 2);

            Assert.NotEmpty(posted);
        }
        finally
        {
            Directory.Delete(audioRoot, recursive: true);
        }
    }

    /// <summary>
    /// A transmitter close enough to overload the front end (your own APRS beacon) lifts the whole
    /// band at once: on air that opened ten gates together and they stayed open long after the
    /// desense passed. While the ADC is clipping nothing in the filterbank is signal, and the
    /// hardware tells us so — so hold every gate shut rather than trying to infer it from levels.
    /// </summary>
    [Fact]
    public void NothingOpensWhileTheFrontEndIsOverloaded()
    {
        var audioRoot = Path.Combine(Path.GetTempPath(), $"ss-ovl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(audioRoot);
        try
        {
            var channelizer = new PolyphaseChannelizer(Fs, Spacing);
            var posted = new List<TransmissionIngest>();
            var discarded = new List<DiscardIngest>();
            var bank = new ChannelBank(
                channelizer.ChannelCount, channelizer.OutputSampleRate, CenterHz,
                bin => channelizer.BinFrequencyHz(bin, CenterHz),
                openDb: 8, closeDb: 5, hangMs: 300,
                audioRoot, DateTime.UtcNow,
                _ => null, posted.Add, NullLogger.Instance, postDiscard: discarded.Add);
            var sink = new ForwardSink(bank);

            channelizer.Process(MakeNoise(0.4), sink);          // settle the floors

            // Only two channels lift, which is under the simultaneous-open threshold — so this
            // isolates the overload gate rather than re-testing the level-event heuristic.
            bank.Overloaded = true;
            channelizer.Process(MakeBandBurst(2, 0.5), sink);    // the beacon keys up
            bank.Overloaded = false;

            channelizer.Process(MakeNoise(0.8), sink);           // and the band comes back
            bank.CloseAll(long.MaxValue / 2);

            Assert.Empty(posted);
            Assert.Empty(discarded);
            Assert.Equal(0, bank.OpenGateCount);

            // The same burst without the overload flag does open gates — otherwise the assertions
            // above would hold for the wrong reason.
            var control = new List<TransmissionIngest>();
            var controlDiscards = new List<DiscardIngest>();
            var chan2 = new PolyphaseChannelizer(Fs, Spacing);
            var unguarded = new ChannelBank(
                chan2.ChannelCount, chan2.OutputSampleRate, CenterHz,
                bin => chan2.BinFrequencyHz(bin, CenterHz),
                openDb: 8, closeDb: 5, hangMs: 300,
                audioRoot, DateTime.UtcNow,
                _ => null, control.Add, NullLogger.Instance, postDiscard: controlDiscards.Add);
            var sink2 = new ForwardSink(unguarded);
            chan2.Process(MakeNoise(0.4), sink2);
            chan2.Process(MakeBandBurst(2, 0.5), sink2);
            chan2.Process(MakeNoise(0.8), sink2);
            unguarded.CloseAll(long.MaxValue / 2);

            Assert.NotEmpty(control.Concat<object>(controlDiscards));
        }
        finally
        {
            Directory.Delete(audioRoot, recursive: true);
        }
    }

    /// <summary>
    /// The floors are re-referenced on the way out of overload, so a real signal arriving right
    /// afterwards must still open a gate — recovery cannot cost us the next transmission.
    /// </summary>
    [Fact]
    public void RealSignalOpensImmediatelyAfterOverloadClears()
    {
        var audioRoot = Path.Combine(Path.GetTempPath(), $"ss-ovl2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(audioRoot);
        try
        {
            var channelizer = new PolyphaseChannelizer(Fs, Spacing);
            var posted = new List<TransmissionIngest>();
            var bank = new ChannelBank(
                channelizer.ChannelCount, channelizer.OutputSampleRate, CenterHz,
                bin => channelizer.BinFrequencyHz(bin, CenterHz),
                openDb: 8, closeDb: 5, hangMs: 300,
                audioRoot, DateTime.UtcNow,
                f => f, posted.Add, NullLogger.Instance);
            var sink = new ForwardSink(bank);

            channelizer.Process(MakeNoise(0.4), sink);
            bank.Overloaded = true;
            channelizer.Process(MakeBandBurst(12, 0.5), sink);
            bank.Overloaded = false;
            channelizer.Process(MakeNoise(0.3), sink);
            channelizer.Process(MakeFmBurst(75_000, 1.5), sink);
            channelizer.Process(MakeNoise(0.5), sink);
            bank.CloseAll(long.MaxValue / 2);

            Assert.NotEmpty(posted);
        }
        finally
        {
            Directory.Delete(audioRoot, recursive: true);
        }
    }

    /// <summary>
    /// The API reports overload after the fact: on air, seven gates opened at peaks up to -1.9 dBFS
    /// in the moments before the event arrived, and one that opened as it cleared stayed up for 49
    /// seconds. So the flag arriving must disown gates opened just before it, and gates must stay
    /// shut through the recovery afterwards rather than opening into the AGC transient.
    /// </summary>
    [Fact]
    public void GatesOpenedJustBeforeAnOverloadIsReportedAreDisowned()
    {
        var audioRoot = Path.Combine(Path.GetTempPath(), $"ss-late-{Guid.NewGuid():N}");
        Directory.CreateDirectory(audioRoot);
        try
        {
            var channelizer = new PolyphaseChannelizer(Fs, Spacing);
            var posted = new List<TransmissionIngest>();
            var discarded = new List<DiscardIngest>();
            var bank = new ChannelBank(
                channelizer.ChannelCount, channelizer.OutputSampleRate, CenterHz,
                bin => channelizer.BinFrequencyHz(bin, CenterHz),
                openDb: 8, closeDb: 5, hangMs: 300,
                audioRoot, DateTime.UtcNow,
                f => f, posted.Add, NullLogger.Instance, postDiscard: discarded.Add);
            var sink = new ForwardSink(bank);

            channelizer.Process(MakeNoise(0.4), sink);

            // The beacon keys up and gates open — the hardware has not told us yet.
            channelizer.Process(MakeBandBurst(2, 0.2), sink);
            Assert.True(bank.OpenGateCount > 0, "expected gates to open before the flag arrives");

            // Now the report lands, late.
            bank.Overloaded = true;
            channelizer.Process(MakeBandBurst(2, 0.2), sink);
            bank.Overloaded = false;
            channelizer.Process(MakeNoise(1.0), sink);
            bank.CloseAll(long.MaxValue / 2);

            Assert.Empty(posted);
            Assert.Equal(0, bank.OpenGateCount);
            Assert.All(discarded, d => Assert.Equal(DiscardReason.FrontEndOverload, d.Reason));
        }
        finally
        {
            Directory.Delete(audioRoot, recursive: true);
        }
    }

    [Fact]
    public void SteadyStateChannelizationAllocatesAlmostNothing()
    {
        var channelizer = new PolyphaseChannelizer(Fs, Spacing);
        var sink = new PowerSink(channelizer.ChannelCount);
        var iq = MakeNoise(0.05);
        channelizer.Process(iq, sink); // warm-up

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 20; i++)
        {
            channelizer.Process(iq, sink);
        }

        var perPass = (GC.GetAllocatedBytesForCurrentThread() - before) / 20.0;
        Assert.True(perPass < 1024, $"channelizer steady state allocated {perPass:F0} B/pass — hot path must not allocate");
    }

    // --- helpers ---

    private static float[] MakeTone(double offsetHz, double seconds)
    {
        var n = (int)(Fs * seconds);
        var iq = new float[n * 2];
        for (var i = 0; i < n; i++)
        {
            var phase = 2 * Math.PI * offsetHz * i / Fs;
            iq[2 * i] = (float)(0.5 * Math.Cos(phase));
            iq[2 * i + 1] = (float)(0.5 * Math.Sin(phase));
        }

        return iq;
    }

    private static float[] MakeToneAtRate(double offsetHz, double rate, double seconds)
    {
        var n = (int)(rate * seconds);
        var iq = new float[n * 2];
        for (var i = 0; i < n; i++)
        {
            var phase = 2 * Math.PI * offsetHz * i / rate;
            iq[2 * i] = (float)(0.7 * Math.Cos(phase));
            iq[2 * i + 1] = (float)(0.7 * Math.Sin(phase));
        }

        return iq;
    }

    private static readonly Random SpeechNoise = new(11);

    /// <summary>Spectrally broad, syllabically modulated audio with pitch drift — real voices never hold one exact bin.</summary>
    private static float SpeechLikeSample(int n)
    {
        var t = n / 16_000.0;
        var syllable = 0.55 + 0.45 * Math.Sin(2 * Math.PI * 4 * t);
        var drift = 1 + 0.06 * Math.Sin(2 * Math.PI * 1.3 * t);
        var formants = 0.5 * Math.Sin(2 * Math.PI * 520 * drift * t)
                     + 0.35 * Math.Sin(2 * Math.PI * 1180 * drift * t + 1.1)
                     + 0.2 * Math.Sin(2 * Math.PI * 2210 * drift * t + 2.3)
                     + 0.1 * (SpeechNoise.NextDouble() * 2 - 1);
        return (float)(syllable * 0.45 * formants);
    }

    /// <summary>
    /// FM burst modulated by band-limited speech-like audio. The audio is generated at 16 kHz and
    /// interpolated up to the IQ rate — generating it per-IQ-sample would put white noise across
    /// the full 3.2 MHz span, splattering into dozens of channels (no real transmitter does that).
    /// </summary>
    private static float[] MakeFmBurst(double offsetHz, double seconds)
    {
        var audioRate = 16_000;
        var audio = new float[(int)(audioRate * seconds) + 2];
        for (var i = 0; i < audio.Length; i++)
        {
            audio[i] = SpeechLikeSample(i);
        }

        var n = (int)(Fs * seconds);
        var iq = new float[n * 2];
        var step = audioRate / Fs;
        var noise = new Random(5);
        double phase = 0, audioPos = 0;
        for (var i = 0; i < n; i++)
        {
            var idx = (int)audioPos;
            var frac = (float)(audioPos - idx);
            var sample = idx + 1 < audio.Length ? audio[idx] + (audio[idx + 1] - audio[idx]) * frac : audio[^1];
            audioPos += step;

            phase += 2 * Math.PI * (offsetHz + 2500 * sample) / Fs;
            iq[2 * i] = (float)(0.4 * Math.Cos(phase) + (noise.NextDouble() * 2 - 1) * NoiseAmplitude);
            iq[2 * i + 1] = (float)(0.4 * Math.Sin(phase) + (noise.NextDouble() * 2 - 1) * NoiseAmplitude);
        }

        return iq;
    }

    private const double NoiseAmplitude = 0.008;

    /// <summary>
    /// A level event across part of the band: many adjacent channels lifting together, the way
    /// interference or a switching preamp looks to the filterbank. Spaced two bins apart so each
    /// carrier is its own local maximum — the adjacent-bin rule must not be what saves us here.
    /// </summary>
    private static float[] MakeBandBurst(int channels, double seconds, double amplitude = 0.02)
    {
        var n = (int)(Fs * seconds);
        var iq = new float[n * 2];
        var rng = new Random(23);
        for (var i = 0; i < n; i++)
        {
            double re = 0, im = 0;
            for (var k = 0; k < channels; k++)
            {
                var offset = 100_000 + k * 2 * Spacing;
                var phase = 2 * Math.PI * offset * i / Fs;
                re += amplitude * Math.Cos(phase);
                im += amplitude * Math.Sin(phase);
            }

            iq[2 * i] = (float)(re + (rng.NextDouble() * 2 - 1) * NoiseAmplitude);
            iq[2 * i + 1] = (float)(im + (rng.NextDouble() * 2 - 1) * NoiseAmplitude);
        }

        return iq;
    }

    /// <summary>
    /// A steady unmodulated carrier sitting on the band noise floor — a birdie, a spur, or a data
    /// transmitter that never unkeys. Noise must be present: a bare tone drops the band median to
    /// the log floor and opens every bin, which is a property of the fixture, not of the squelch.
    /// </summary>
    private static float[] MakeCarrierOnNoise(double offsetHz, double seconds, double amplitude = 0.4)
    {
        var rng = new Random(17);
        var n = (int)(Fs * seconds);
        var iq = new float[n * 2];
        for (var i = 0; i < n; i++)
        {
            var phase = 2 * Math.PI * offsetHz * i / Fs;
            iq[2 * i] = (float)(amplitude * Math.Cos(phase) + (rng.NextDouble() * 2 - 1) * NoiseAmplitude);
            iq[2 * i + 1] = (float)(amplitude * Math.Sin(phase) + (rng.NextDouble() * 2 - 1) * NoiseAmplitude);
        }

        return iq;
    }

    private static float[] MakeNoise(double seconds, double amplitude = NoiseAmplitude)
    {
        var rng = new Random(3);
        var n = (int)(Fs * seconds);
        var iq = new float[n * 2];
        for (var i = 0; i < iq.Length; i++)
        {
            iq[i] = (float)((rng.NextDouble() * 2 - 1) * amplitude);
        }

        return iq;
    }

    private static float[] MakeAudio(int samples, Func<int, float> generator)
    {
        var pcm = new float[samples];
        for (var i = 0; i < samples; i++)
        {
            pcm[i] = generator(i);
        }

        return pcm;
    }

    private sealed class PowerSink(int channels) : IChannelizerSink
    {
        private readonly double[] _power = new double[channels];

        private long _hops;

        public void OnHop(ReadOnlySpan<float> frame, long hopIndex)
        {
            for (var c = 0; c < _power.Length; c++)
            {
                _power[c] += frame[2 * c] * frame[2 * c] + frame[2 * c + 1] * frame[2 * c + 1];
            }

            _hops++;
        }

        public double[] AveragePowerDb()
        {
            var db = new double[_power.Length];
            for (var c = 0; c < _power.Length; c++)
            {
                db[c] = 10 * Math.Log10(_power[c] / Math.Max(1, _hops) + 1e-20);
            }

            return db;
        }
    }

    private sealed class ForwardSink(ChannelBank bank) : IChannelizerSink
    {
        public void OnHop(ReadOnlySpan<float> frame, long hopIndex) => bank.OnHop(frame, hopIndex);
    }

    private sealed class HopSink(ChannelBank bank) : IChannelizerSink
    {
        public long LastHop { get; private set; }

        public void OnHop(ReadOnlySpan<float> frame, long hopIndex)
        {
            LastHop = hopIndex;
            bank.OnHop(frame, hopIndex);
        }
    }
}
