using SignalScribe.Capture.Digital.Ysf;
using SignalScribe.Capture.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

/// <summary>
/// Fusion through the real capture chain at a range of carrier offsets — the experiment that says
/// whether 145.310 fails because of where it sits on the analysis grid.
///
/// The noise sweep established that the on-air sync rate (three or four frames in ten) corresponds
/// to roughly a 20% symbol error rate, at which no FICH can survive whatever the decoder's
/// conventions are. This asks where that error rate comes from. 145.310 lands 2.5 kHz off its
/// 145.3125 filterbank bin, and the channel filter is already known to compress the outer C4FM
/// symbols by about 6% there — which matters far more for a four-level eye than a two-level one,
/// because the inner and outer levels are only 1.2 kHz apart to begin with.
/// </summary>
public class YsfOffGridDiag(ITestOutputHelper output)
{
    private const double Fs = 3_200_000;
    private const double Spacing = 12_500;
    private const double Baud = 4_800;
    private const double OuterHz = 1_800;
    private const double InnerHz = 600;

    [Fact]
    public void SweepCarrierOffset()
    {
        output.WriteLine("  offset   syncs   FICH   mode");

        foreach (var offset in new[] { 0.0, 1_250.0, 2_500.0, -2_500.0, 3_750.0, 5_000.0 })
        {
            var (syncs, fich, mode) = RunThroughChain(offset, frames: 20);
            output.WriteLine($"  {offset,6:F0} {syncs,7} {fich,6}   {mode}");
        }

        output.WriteLine(string.Empty);
        output.WriteLine("  145.310 sits 2500 Hz off its 145.3125 bin. Compare that row against 0.");
    }

    private (int Syncs, int Fich, string Mode) RunThroughChain(double offsetHz, int frames)
    {
        var deviation = Render(frames);
        var channelizer = new PolyphaseChannelizer(Fs, Spacing);
        var sink = new PeakBinSink(channelizer.ChannelCount);
        channelizer.Process(Modulate(deviation, (32 * Spacing) + offsetHz), sink);

        var demod = new NbfmDemodulator(channelizer.OutputSampleRate, decodeDigital: true);
        var pcm = new float[16_000 * 10];
        demod.Process(sink.Samples(sink.PeakBin()), pcm);

        // The framer's own counters are behind the demodulator; the summary string carries them.
        var summary = demod.YsfSummary;
        var syncs = int.Parse(summary.Split(' ')[0]);
        var fich = int.Parse(summary.Split('/')[1].Trim().Split(' ')[0]);
        return (syncs, fich, demod.Mode.ToString());
    }

    /// <summary>A run of Fusion frames as a shaped four-level deviation waveform at 25 kSPS.</summary>
    private static float[] Render(int frames)
    {
        var rng = new Random(11);
        var symbols = new List<double>(frames * 480);
        for (var frame = 0; frame < frames; frame++)
        {
            for (var i = 19; i >= 0; i--)
            {
                symbols.Add(Level((int)((YsfFramer.FrameSync >> (i * 2)) & 3)));
            }

            var soft = new double[YsfFichDecoder.Dibits * 2];
            YsfFichDecoder.Encode([0x20, 0x01, (byte)(0x08 | (frame & 7)), 0x40], YsfFichDecoder.Variants[0], soft);
            for (var i = 0; i < YsfFichDecoder.Dibits; i++)
            {
                symbols.Add(Level((soft[2 * i] > 0.5 ? 2 : 0) + (soft[(2 * i) + 1] > 0.5 ? 1 : 0)));
            }

            for (var i = 0; i < 360; i++)
            {
                symbols.Add(Level(rng.Next(4)));
            }
        }

        return Shape([.. symbols]);
    }

    private static double Level(int dibit) => dibit switch
    {
        0b01 => OuterHz,
        0b00 => InnerHz,
        0b10 => -InnerHz,
        _ => -OuterHz,
    };

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
                sum += symbols[k] * RaisedCosine(centre - k, 0.2);
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
