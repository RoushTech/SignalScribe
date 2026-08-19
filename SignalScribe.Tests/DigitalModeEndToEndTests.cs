using Microsoft.Extensions.Logging.Abstractions;
using SignalScribe.Capture.Dsp;
using SignalScribe.Contracts;
using SignalScribe.Enums;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

/// <summary>
/// Digital traffic through the whole capture chain — channelizer, squelch, demodulator, classifier,
/// gate — rather than the classifier alone. This is what proves the change to the voice gate does
/// what it is supposed to: a mode we can name earns a channel, and one we cannot still does not.
/// </summary>
public class DigitalModeEndToEndTests(ITestOutputHelper output)
{
    private const double Fs = 3_200_000;
    private const double Spacing = 12_500;
    private const long CenterHz = 146_000_000;
    private const double NoiseAmplitude = 0.008;

    [Fact]
    public void DStarOnAnUnknownFrequencyEarnsAChannelInsteadOfBeingDiscarded()
    {
        // Before this change a D-STAR burst failed the voice gate — no syllables, no speech band —
        // and was thrown away, so the frequency stayed invisible forever. Naming the mode is what
        // makes it worth keeping.
        var (posted, discarded) = Run(DigitalSignals.Fsk(25_000, seconds: 2.0, deviation: 1_200, baud: 4_800, seed: 1, rolloff: 0.5));

        Assert.Empty(discarded);
        var tx = Assert.Single(posted);
        Assert.Equal(DetectedMode.DStar, tx.Mode);
        output.WriteLine($"  posted {tx.FrequencyHz} Hz as {tx.Mode}");
    }

    [Fact]
    public void FourLevelTrafficIsHeldBackUntilAFramerCanNameIt()
    {
        // DMR through the real chain measures as digital but not as DMR — the channel filter
        // compresses the outer symbols by 4-6%, which is the same order as the gap between DMR's
        // deviation plan and P25's. It is held with the other unidentified digital rather than
        // guessed at, and the framers will name it in due course.
        var (posted, discarded) = Run(DigitalSignals.C4fm(
            25_000, seconds: 2.0, DigitalSignals.DmrOuterHz, DigitalSignals.DmrInnerHz, DigitalSignals.C4fmBaud, seed: 3));

        Assert.Empty(posted);
        var clip = Assert.Single(discarded);
        Assert.Equal(DetectedMode.DigitalUnknown, clip.Mode);
        Assert.Equal(DiscardReason.DigitalNotIdentified, clip.Reason);
    }

    [Fact]
    public void UnidentifiedDigitalOnAnUnknownFrequencyIsStillDiscarded()
    {
        // An AFSK packet reads as an anonymous two-level signal until the soft-TNC decodes a frame.
        // It must not create a channel on that basis — that is exactly how 144.390 came to record
        // 1072 packets, and the discard now says *why* rather than blaming the speech band.
        var (posted, discarded) = Run(DigitalSignals.Afsk(25_000, seconds: 2.0, deviation: 3_000, seed: 2));

        Assert.Empty(posted);
        var clip = Assert.Single(discarded);
        Assert.Equal(DiscardReason.DigitalNotIdentified, clip.Reason);
        Assert.Equal(DetectedMode.DigitalUnknown, clip.Mode);
        output.WriteLine($"  discarded as {clip.Reason} ({clip.Mode})");
    }

    [Fact]
    public void AnalogSpeechIsUnaffectedByTheModeClassifier()
    {
        // The whole point is to add a second way through the gate, not to change the first one.
        var deviation = new float[(int)(25_000 * 2.0)];
        for (var i = 0; i < deviation.Length; i++)
        {
            var t = i / 25_000.0;
            var envelope = 0.5 + (0.5 * Math.Sin(2 * Math.PI * 4 * t));
            deviation[i] = (float)(2_500 * envelope *
                ((0.6 * Math.Sin(2 * Math.PI * 300 * t)) + (0.3 * Math.Sin(2 * Math.PI * 900 * t)) + (0.2 * Math.Sin(2 * Math.PI * 1_700 * t))));
        }

        var (posted, _) = Run(deviation);
        var tx = Assert.Single(posted);
        Assert.Equal(DetectedMode.AnalogFm, tx.Mode);
    }

    /// <summary>
    /// Frequency-modulates a deviation waveform onto a carrier one bin up from centre, pushes it
    /// through a real channelizer and bank on an <em>unknown</em> frequency, and returns what came out.
    /// </summary>
    private static (List<TransmissionIngest> Posted, List<DiscardIngest> Discarded) Run(float[] deviationHz)
    {
        var audioRoot = Path.Combine(Path.GetTempPath(), $"ss-digital-{Guid.NewGuid():N}");
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
                audioRoot, new DateTime(2026, 8, 16, 0, 0, 0, DateTimeKind.Utc),
                _ => null, // UNKNOWN frequency — the gate has to decide on its own
                posted.Add,
                NullLogger.Instance,
                postDiscard: discarded.Add);

            var sink = new ForwardingSink(bank);
            channelizer.Process(Noise(0.4), sink);
            channelizer.Process(Modulate(deviationHz, 32 * Spacing), sink);
            channelizer.Process(Noise(0.8), sink);

            return (posted, discarded);
        }
        finally
        {
            Directory.Delete(audioRoot, recursive: true);
        }
    }

    /// <summary>
    /// FM-modulates a deviation waveform (given at 25 kSPS, the per-channel rate) onto an offset
    /// carrier at the wideband rate, interpolating between deviation samples.
    /// </summary>
    private static float[] Modulate(float[] deviationHz, double offsetHz)
    {
        const double DeviationRate = 25_000;
        var seconds = deviationHz.Length / DeviationRate;
        var n = (int)(Fs * seconds);
        var iq = new float[n * 2];
        var step = DeviationRate / Fs;
        var noise = new Random(5);

        double phase = 0, position = 0;
        for (var i = 0; i < n; i++)
        {
            var index = (int)position;
            var frac = (float)(position - index);
            var deviation = index + 1 < deviationHz.Length
                ? deviationHz[index] + ((deviationHz[index + 1] - deviationHz[index]) * frac)
                : deviationHz[^1];
            position += step;

            phase += 2 * Math.PI * (offsetHz + deviation) / Fs;
            iq[2 * i] = (float)((0.4 * Math.Cos(phase)) + (((noise.NextDouble() * 2) - 1) * NoiseAmplitude));
            iq[(2 * i) + 1] = (float)((0.4 * Math.Sin(phase)) + (((noise.NextDouble() * 2) - 1) * NoiseAmplitude));
        }

        return iq;
    }

    private static float[] Noise(double seconds)
    {
        var n = (int)(Fs * seconds);
        var iq = new float[n * 2];
        var rng = new Random(9);
        for (var i = 0; i < iq.Length; i++)
        {
            iq[i] = (float)(((rng.NextDouble() * 2) - 1) * NoiseAmplitude);
        }

        return iq;
    }

    private sealed class ForwardingSink(ChannelBank bank) : IChannelizerSink
    {
        private long _hop;

        public void OnHop(ReadOnlySpan<float> channels, long hopIndex) => bank.OnHop(channels, _hop++);
    }
}
