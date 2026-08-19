using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using SignalScribe.Capture.Digital.DStar;
using SignalScribe.Capture.Dsp;
using SignalScribe.Contracts;
using SignalScribe.Enums;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

/// <summary>
/// A D-STAR header through everything: GMSK onto a carrier, the real channelizer, the discriminator,
/// symbol timing recovery, sync detection, and the FEC chain — ending in callsigns.
///
/// This is where the pieces stop being components. The symbol synchroniser has to lock from the
/// preamble, hold through the header, and hand the framer symbols good enough for a CRC to pass, all
/// while the carrier sits off its filterbank bin the way a real one does.
/// </summary>
public class DStarThroughCaptureChainTests(ITestOutputHelper output)
{
    private const double Fs = 3_200_000;
    private const double Spacing = 12_500;
    private const double Baud = 4_800;
    private const double DeviationHz = 1_200;

    [Theory]
    [InlineData(0)]
    [InlineData(1_250)]
    [InlineData(-1_250)]
    [InlineData(-2_500)]
    public void RecoversCallsignsNearTheBinCentre(double offsetHz)
    {
        var headers = RunThroughChain("KD9ABC  ", "CQCQCQ  ", offsetHz);

        var header = Assert.Single(headers);
        Assert.Equal("KD9ABC", header.Mycall);
        Assert.Equal("CQCQCQ", header.Urcall);
        output.WriteLine($"  offset {offsetHz,6:F0} Hz -> {header.MycallWithSuffix} → {header.Urcall} via {header.RepeaterSource}");
    }

    /// <summary>
    /// Across the whole ±5 kHz the 12.5 kHz analysis grid allows, most offsets recover the header and
    /// a couple do not — the sync is found every time, and what fails is the CRC on a symbol stream
    /// that came out marginal.
    ///
    /// Asserted as a rate rather than a list of the offsets that happen to work, because pinning the
    /// passing ones would be fitting the test to the code and would hide a regression that merely
    /// moved which offsets fail. The two-of-nine shortfall is real and is not tuned away here: with
    /// the frame sync and interleave orientation still unconfirmed against real air, tuning against a
    /// synthetic signal would be optimising something that may itself be wrong.
    /// </summary>
    [Fact]
    public void RecoversHeadersAcrossMostOfTheOffGridRange()
    {
        double[] offsets = [0, 1_250, 2_500, 3_750, 5_000, -1_250, -2_500, -3_750, -5_000];

        var recovered = 0;
        foreach (var offset in offsets)
        {
            var headers = RunThroughChain("KD9ABC  ", "CQCQCQ  ", offset);
            var ok = headers.Count == 1 && headers[0].Mycall == "KD9ABC";
            recovered += ok ? 1 : 0;
            output.WriteLine($"  offset {offset,6:F0} Hz -> {(ok ? "KD9ABC" : "no header")}");
        }

        output.WriteLine($"  {recovered}/{offsets.Length} offsets recovered");
        Assert.True(recovered >= 7, $"only {recovered}/{offsets.Length} offsets recovered a header");
    }

    [Fact]
    public void NamesTheModeFromTheDecodeRatherThanTheHistogram()
    {
        var demod = Demodulate("W9XYZ   ", "CQCQCQ  ", offsetHz: 1_250);

        // The level histogram would say D-STAR here too, but a decoded header is proof rather than
        // evidence — and on a mode the histogram cannot name, the decode is the only thing that can.
        Assert.Equal(DetectedMode.DStar, demod.Mode);
        Assert.Single(demod.DStarHeaders);
    }

    /// <summary>
    /// A D-STAR transmission on an unknown frequency earns a channel *and* arrives carrying the
    /// callsigns — the whole point of the mode work, through the real gate.
    ///
    /// This is the test the pre-roll buffer exists for. The squelch gate waits two blocks (~20 ms)
    /// for a carrier to persist before opening, and a D-STAR header is over within the first ~25 ms,
    /// so without pre-roll the demodulator was handed its first sample after the header had already
    /// gone past and this recovered nothing.
    /// </summary>
    [Fact]
    public void ADStarTransmissionOnAnUnknownFrequencyPostsWithItsCallsigns()
    {
        var audioRoot = Path.Combine(Path.GetTempPath(), $"ss-dstar-{Guid.NewGuid():N}");
        Directory.CreateDirectory(audioRoot);
        try
        {
            var channelizer = new PolyphaseChannelizer(Fs, Spacing);
            var posted = new List<TransmissionIngest>();
            var discarded = new List<DiscardIngest>();
            var bank = new ChannelBank(
                channelizer.ChannelCount, channelizer.OutputSampleRate, 146_000_000,
                bin => channelizer.BinFrequencyHz(bin, 146_000_000),
                openDb: 8, closeDb: 5, hangMs: 300,
                audioRoot, new DateTime(2026, 8, 17, 0, 0, 0, DateTimeKind.Utc),
                _ => null, // unknown frequency — the gate decides
                posted.Add,
                NullLogger.Instance,
                postDiscard: discarded.Add);

            var sink = new ForwardingSink(bank);
            channelizer.Process(Silence(0.4), sink);
            // A header on its own lasts ~190 ms, which the gate rejects as too short to be a real
            // transmission — and rightly so. A D-STAR radio sends voice frames for as long as the
            // operator holds the key, so the burst has to be a realistic length to reach the gate.
            channelizer.Process(Modulate(RenderTransmission("KD9ABC  ", "CQCQCQ  ", trailingSymbols: 9_600), (32 * Spacing) + 1_250), sink);
            channelizer.Process(Silence(0.8), sink);

            output.WriteLine($"  posted {posted.Count}, discarded {discarded.Count}");
            foreach (var d in discarded)
            {
                output.WriteLine($"    discard {d.FrequencyHz} {d.Reason} {d.Mode} {(d.EndUtc - d.StartUtc).TotalMilliseconds:F0} ms");
            }

            // Splatter into a neighbouring bin can produce its own short discard; what matters is
            // that the transmission itself was posted rather than thrown away.
            var tx = Assert.Single(posted);
            Assert.Equal(DetectedMode.DStar, tx.Mode);
            Assert.DoesNotContain(discarded, d => d.FrequencyHz == tx.FrequencyHz);

            // The header survives the gate now: the pre-roll buffer hands the demodulator the audio
            // from before the carrier was noticed, which is where the header lives.
            var header = Assert.Single(tx.DigitalHeaders!);
            Assert.Equal(DetectedMode.DStar, header.Mode);
            Assert.Equal("KD9ABC", header.Callsign);

            // The whole header reaches the host, not just the summary: on a mode whose voice we
            // cannot decode, these fields are the entire content of the transmission.
            Assert.Equal("KD9ABC", Field(header, "My call"));
            Assert.Equal("CQCQCQ", Field(header, "Your call"));
            // RPT1 is the module the traffic entered through, RPT2 the gateway it is bound for —
            // the fixture's "  B" module and "  G" gateway, the way a real D-STAR stack is written.
            Assert.Equal("W9XYZ  B", Field(header, "Repeater in"));
            Assert.Equal("W9XYZ  G", Field(header, "Repeater out"));
            Assert.Equal("Group call", Field(header, "Call type"));
            Assert.Equal("No", Field(header, "Emergency"));
            output.WriteLine($"  {tx.FrequencyHz} Hz {tx.Mode}: {header.Summary}");
            foreach (var f in header.Fields)
            {
                output.WriteLine($"    {f.Name}: {f.Value}");
            }
        }
        finally
        {
            Directory.Delete(audioRoot, recursive: true);
        }
    }

    private static string? Field(DigitalHeaderIngest header, string name) =>
        header.Fields.FirstOrDefault(f => f.Name == name)?.Value;

    private static float[] Silence(double seconds)
    {
        var n = (int)(Fs * seconds);
        var iq = new float[n * 2];
        var rng = new Random(3);
        for (var i = 0; i < iq.Length; i++)
        {
            iq[i] = (float)(((rng.NextDouble() * 2) - 1) * 0.008);
        }

        return iq;
    }

    private sealed class ForwardingSink(ChannelBank bank) : IChannelizerSink
    {
        private long _hop;

        public void OnHop(ReadOnlySpan<float> channels, long hopIndex) => bank.OnHop(channels, _hop++);
    }

    /// <summary>
    /// The regression behind the cadence requirement. A long repeater conversation — overs of
    /// speech-shaped FM separated by hang-time noise — feeds the framer noise-shaped symbols for
    /// tens of seconds, and the 24-bit voice sync at two errors' tolerance in both polarities trips
    /// on that about every six seconds. The raw hit count crosses the old threshold of three, which
    /// is exactly how a plain FM transmission was named D-STAR and silently excluded from
    /// transcription. Only hits on the 420 ms cadence may name the mode; scattered accidents must
    /// leave the histogram's analog verdict standing.
    /// </summary>
    [Fact]
    public void ALongAnalogConversationWithHangNoiseIsNotNamedDStar()
    {
        const double Rate = 25_000;
        const int Seconds = 180;
        var deviation = new float[(int)(Rate * Seconds)];
        var rng = new Random(9);

        // Short overs and long unmodulated-carrier hang between them — a slow ragchew. The voice is
        // a pitch fundamental with moving formant-ish overtones under a syllabic envelope; the hang
        // is the discriminator's white noise, which is what the symbol slicer turns into coin-flip
        // bits, and is where the accidental sync hits come from.
        for (var i = 0; i < deviation.Length; i++)
        {
            var t = i / Rate;
            var phase = t % 4.5;
            if (phase < 1.5)
            {
                var envelope = 0.55 + (0.45 * Math.Sin(2 * Math.PI * 3.1 * t));
                var voice = (Math.Sin(2 * Math.PI * 140 * t) * 0.5)
                    + (Math.Sin(2 * Math.PI * (700 + (80 * Math.Sin(2 * Math.PI * 0.7 * t))) * t) * 0.35)
                    + (Math.Sin(2 * Math.PI * 1_900 * t) * 0.15);
                deviation[i] = (float)(2_500 * envelope * voice);
            }
            else
            {
                deviation[i] = (float)(1_800 * ((rng.NextDouble() * 2) - 1));
            }
        }

        var demod = new NbfmDemodulator(25_000, decodeDigital: true);
        var pcm = new float[16_000 * (Seconds + 2)];
        demod.Process(ToIq(deviation), pcm);

        output.WriteLine($"  {demod.DStarFrameSyncCount} raw voice-frame syncs, longest cadenced run {demod.DStarCadencedFrameSyncCount}, mode {demod.Mode}");
        Assert.True(demod.DStarFrameSyncCount >= DStarFramer.MinFrameSyncs,
            $"only {demod.DStarFrameSyncCount} raw hits — the fixture no longer arms the trap this test exists for");
        Assert.Equal(DetectedMode.AnalogFm, demod.Mode);
    }

    [Fact]
    public void AnalogVoiceProducesNoHeaders()
    {
        var deviation = new float[(int)(25_000 * 1.5)];
        for (var i = 0; i < deviation.Length; i++)
        {
            var t = i / 25_000.0;
            var envelope = 0.5 + (0.5 * Math.Sin(2 * Math.PI * 4 * t));
            deviation[i] = (float)(2_500 * envelope * Math.Sin(2 * Math.PI * 400 * t));
        }

        var demod = new NbfmDemodulator(25_000, decodeDigital: true);
        var pcm = new float[32_000];
        demod.Process(ToIq(deviation), pcm);

        Assert.Empty(demod.DStarHeaders);
    }

    [Fact]
    public void CostPerGateIsFarBelowTheSoftTnc()
    {
        // The soft TNC costs ~4% of a core per gate, which is why it has to be rationed. This path is
        // a timing loop plus a 24-bit comparison per symbol, with the Viterbi decoder running only
        // when a sync actually matches — so it should be cheap enough not to need a budget at all.
        const int Seconds = 30;
        var deviation = new float[(int)(25_000 * Seconds)];
        var rng = new Random(3);
        for (var i = 0; i < deviation.Length; i++)
        {
            deviation[i] = rng.Next(2) == 0 ? 1_200 : -1_200;
        }

        var iq = ToIq(deviation);
        var pcm = new float[16_000 * (Seconds + 1)];

        var warm = new NbfmDemodulator(25_000, decodeDigital: true);
        warm.Process(iq.AsSpan(0, 50_000), pcm);

        var best = double.MaxValue;
        for (var pass = 0; pass < 3; pass++)
        {
            var demod = new NbfmDemodulator(25_000, decodeDigital: true);
            var sw = Stopwatch.StartNew();
            demod.Process(iq, pcm);
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalSeconds);
        }

        var fraction = best / Seconds;
        output.WriteLine($"  {best * 1000:F0} ms for {Seconds}s (best of 3)");
        output.WriteLine($"  {fraction * 100:F3}% of one core per gate, {fraction * 100 * 32:F2}% with all 32 open");

        // Loose, like the packet cost test: the suite runs in parallel and a tight bound would flake.
        // This catches an order-of-magnitude regression, not a budget breach.
        Assert.True(fraction < 0.05, $"cost {fraction:P2} of one core per gate");
    }

    private static IReadOnlyList<DStarHeader> RunThroughChain(string mycall, string urcall, double offsetHz)
        => Demodulate(mycall, urcall, offsetHz).DStarHeaders;

    private static NbfmDemodulator Demodulate(string mycall, string urcall, double offsetHz)
    {
        var deviation = RenderTransmission(mycall, urcall);

        var channelizer = new PolyphaseChannelizer(Fs, Spacing);
        var sink = new PeakBinSink(channelizer.ChannelCount);
        channelizer.Process(Modulate(deviation, (32 * Spacing) + offsetHz), sink);

        var demod = new NbfmDemodulator(channelizer.OutputSampleRate, decodeDigital: true);
        var pcm = new float[32_000];
        demod.Process(sink.Samples(sink.PeakBin()), pcm);
        return demod;
    }

    /// <summary>Preamble, frame sync and coded header as a shaped 4800-baud deviation waveform at 25 kSPS.</summary>
    private static float[] RenderTransmission(string mycall, string urcall, int trailingSymbols = 128)
    {
        var header = BuildHeader(mycall, urcall);
        var coded = new byte[DStarHeaderFec.ChannelBits];
        DStarHeaderFec.TryEncode(header, coded);

        var bits = new List<int>(1_024);
        for (var i = 0; i < 96; i++)
        {
            bits.Add(i % 2);    // bit sync
        }

        for (var i = 23; i >= 0; i--)
        {
            bits.Add((int)((DStarFramer.HeaderFrameSync >> i) & 1));
        }

        foreach (var b in coded)
        {
            bits.Add(b);
        }

        var rng = new Random(11);
        for (var i = 0; i < trailingSymbols; i++)
        {
            bits.Add(rng.Next(2));   // voice frames follow the header on a real transmission
        }

        var symbols = bits.Select(b => b == 1 ? DeviationHz : -DeviationHz).ToArray();
        return Shape(symbols);
    }

    /// <summary>Raised-cosine shaping — GMSK's gentler transitions, at the channel rate.</summary>
    private static float[] Shape(double[] symbols)
    {
        const double Rate = 25_000;
        var samplesPerSymbol = Rate / Baud;
        var n = (int)((symbols.Length - 8) * samplesPerSymbol);
        var waveform = new float[n];

        for (var i = 0; i < n; i++)
        {
            var centre = i / samplesPerSymbol;
            var first = Math.Max(0, (int)Math.Floor(centre) - 4);
            var last = Math.Min(symbols.Length - 1, (int)Math.Ceiling(centre) + 4);

            double sum = 0;
            for (var k = first; k <= last; k++)
            {
                sum += symbols[k] * RaisedCosine(centre - k, 0.5);
            }

            waveform[i] = (float)sum;
        }

        return waveform;
    }

    private static double RaisedCosine(double u, double beta)
    {
        if (Math.Abs(u) < 1e-9)
        {
            return 1.0;
        }

        var scaled = 2 * beta * u;
        var denominator = 1 - (scaled * scaled);
        if (Math.Abs(denominator) < 1e-9)
        {
            return Math.PI / 4 * Sinc(1 / (2 * beta));
        }

        return Sinc(u) * Math.Cos(Math.PI * beta * u) / denominator;
    }

    private static double Sinc(double u) => Math.Abs(u) < 1e-9 ? 1.0 : Math.Sin(Math.PI * u) / (Math.PI * u);

    /// <summary>Deviation straight onto complex baseband at the channel rate — no channelizer involved.</summary>
    private static float[] ToIq(float[] deviationHz)
    {
        var iq = new float[deviationHz.Length * 2];
        double phase = 0;
        for (var i = 0; i < deviationHz.Length; i++)
        {
            phase += 2 * Math.PI * deviationHz[i] / 25_000;
            iq[2 * i] = (float)(0.4 * Math.Cos(phase));
            iq[(2 * i) + 1] = (float)(0.4 * Math.Sin(phase));
        }

        return iq;
    }

    private static float[] Modulate(float[] deviationHz, double offsetHz)
    {
        const double DeviationRate = 25_000;
        var n = (int)(Fs * (deviationHz.Length / DeviationRate));
        var iq = new float[n * 2];
        var step = DeviationRate / Fs;
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
            iq[2 * i] = (float)(0.4 * Math.Cos(phase));
            iq[(2 * i) + 1] = (float)(0.4 * Math.Sin(phase));
        }

        return iq;
    }

    private static byte[] BuildHeader(string mycall, string urcall)
    {
        var header = new byte[DStarHeader.Length];
        Write(header.AsSpan(3, 8), "W9XYZ  G");
        Write(header.AsSpan(11, 8), "W9XYZ  B");
        Write(header.AsSpan(19, 8), urcall);
        Write(header.AsSpan(27, 8), mycall);
        Write(header.AsSpan(35, 4), "    ");
        DStarHeader.StampCrc(header);
        return header;
    }

    private static void Write(Span<byte> field, string text)
    {
        field.Fill((byte)' ');
        Encoding.ASCII.GetBytes(text.PadRight(field.Length)[..field.Length], field);
    }

    private sealed class PeakBinSink(int channels) : IChannelizerSink
    {
        private readonly List<float>[] _buf = [.. Enumerable.Range(0, channels).Select(_ => new List<float>())];
        private readonly double[] _power = new double[channels];

        public void OnHop(ReadOnlySpan<float> frame, long hopIndex)
        {
            for (var c = 0; c < _power.Length; c++)
            {
                _buf[c].Add(frame[2 * c]);
                _buf[c].Add(frame[(2 * c) + 1]);
                _power[c] += (frame[2 * c] * frame[2 * c]) + (frame[(2 * c) + 1] * frame[(2 * c) + 1]);
            }
        }

        public int PeakBin()
        {
            var best = 1;
            for (var c = 1; c < _power.Length; c++)
            {
                if (_power[c] > _power[best])
                {
                    best = c;
                }
            }

            return best;
        }

        public float[] Samples(int bin) => [.. _buf[bin]];
    }
}
