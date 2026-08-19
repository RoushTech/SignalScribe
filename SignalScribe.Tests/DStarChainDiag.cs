using System.Text;
using SignalScribe.Capture.Digital.DStar;
using SignalScribe.Capture.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

/// <summary>
/// Diagnostic: where a D-STAR header is lost between the carrier and the callsigns. Prints, does not
/// assert. Distinguishes "sync never seen" from "sync seen, CRC rejected it", which are different
/// faults with different fixes.
/// </summary>
public class DStarChainDiag(ITestOutputHelper output)
{
    private const double Fs = 3_200_000;
    private const double Spacing = 12_500;
    private const double Baud = 4_800;

    [Theory]
    [InlineData(32)]    // even bin
    [InlineData(33)]    // odd bin — the WOLA structure negates odd bins on odd hops
    public void WhereIsTheHeaderLost(int binIndex)
    {
        output.WriteLine($"bin {binIndex} ({(binIndex % 2 == 0 ? "even" : "odd")}):");
        foreach (var offset in new[] { 0.0, 1_250, 2_500, 3_750, 5_000, -1_250, -2_500, -3_750, -5_000 })
        {
            var deviation = Render();

            var channelizer = new PolyphaseChannelizer(Fs, Spacing);
            var sink = new PeakBinSink(channelizer.ChannelCount);
            channelizer.Process(Modulate(deviation, (binIndex * Spacing) + offset), sink);

            // Reproduce the demodulator's discriminator + carrier removal, then run the symbol
            // recovery and framer directly so their counters are visible.
            var channelSamples = sink.Samples(sink.PeakBin());
            var rate = channelizer.OutputSampleRate;
            var audio = Discriminate(channelSamples, rate);

            var sync = new SymbolSynchronizer(rate, Baud);
            var framer = new DStarFramer();
            var headers = new List<DStarHeader>();
            framer.HeaderDecoded += headers.Add;

            var symbols = 0;
            foreach (var s in audio)
            {
                if (sync.Feed(s, out var symbol))
                {
                    symbols++;
                    framer.Feed(symbol);
                }
            }

            output.WriteLine(
                $"  offset {offset,6:F0} Hz: {symbols,5} symbols, {framer.SyncCount} sync, " +
                $"{framer.HeaderCount} headers, {sync.ClockErrorPpm,7:F0} ppm  {(headers.Count > 0 ? headers[0].Mycall : "-")}");
        }
    }

    /// <summary>Discriminator with the slow carrier removal the demodulator applies, normalised the same way.</summary>
    private static float[] Discriminate(float[] iq, double rate)
    {
        var outBuf = new float[(iq.Length / 2) - 1];
        float prevI = iq[0], prevQ = iq[1];
        var scale = rate / (2 * Math.PI);
        var alpha = 1.0 - Math.Exp(-1.0 / (rate * 0.2));
        double dc = 0;
        var seeded = false;
        var n = 0;

        for (var s = 2; s + 1 < iq.Length; s += 2)
        {
            var i = iq[s];
            var q = iq[s + 1];
            var freq = Math.Atan2((prevI * q) - (prevQ * i), (prevI * i) + (prevQ * q)) * scale;
            prevI = i;
            prevQ = q;

            if (!seeded)
            {
                dc = freq;
                seeded = true;
            }
            else
            {
                dc += alpha * (freq - dc);
            }

            outBuf[n++] = (float)((freq - dc) / 5_000);
        }

        return outBuf[..n];
    }

    private static float[] Render()
    {
        var header = new byte[DStarHeader.Length];
        Write(header.AsSpan(3, 8), "W9XYZ  G");
        Write(header.AsSpan(11, 8), "W9XYZ  B");
        Write(header.AsSpan(19, 8), "CQCQCQ  ");
        Write(header.AsSpan(27, 8), "KD9ABC  ");
        Write(header.AsSpan(35, 4), "    ");
        DStarHeader.StampCrc(header);

        var coded = new byte[DStarHeaderFec.ChannelBits];
        DStarHeaderFec.TryEncode(header, coded);

        var bits = new List<int>();
        for (var i = 0; i < 96; i++)
        {
            bits.Add(i % 2);
        }

        for (var i = 23; i >= 0; i--)
        {
            bits.Add((int)((DStarFramer.HeaderFrameSync >> i) & 1));
        }

        bits.AddRange(coded.Select(b => (int)b));
        var rng = new Random(11);
        for (var i = 0; i < 128; i++)
        {
            bits.Add(rng.Next(2));
        }

        var symbols = bits.Select(b => b == 1 ? 1_200.0 : -1_200.0).ToArray();
        const double Rate = 25_000;
        var sps = Rate / Baud;
        var n = (int)((symbols.Length - 8) * sps);
        var waveform = new float[n];
        for (var i = 0; i < n; i++)
        {
            var centre = i / sps;
            var first = Math.Max(0, (int)Math.Floor(centre) - 4);
            var last = Math.Min(symbols.Length - 1, (int)Math.Ceiling(centre) + 4);
            double sum = 0;
            for (var k = first; k <= last; k++)
            {
                sum += symbols[k] * Rc(centre - k, 0.5);
            }

            waveform[i] = (float)sum;
        }

        return waveform;
    }

    private static double Rc(double u, double beta)
    {
        if (Math.Abs(u) < 1e-9)
        {
            return 1.0;
        }

        var scaled = 2 * beta * u;
        var den = 1 - (scaled * scaled);
        if (Math.Abs(den) < 1e-9)
        {
            return Math.PI / 4 * Sinc(1 / (2 * beta));
        }

        return Sinc(u) * Math.Cos(Math.PI * beta * u) / den;
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
            var d = index + 1 < deviationHz.Length
                ? deviationHz[index] + ((deviationHz[index + 1] - deviationHz[index]) * frac)
                : deviationHz[^1];
            position += step;
            phase += 2 * Math.PI * (offsetHz + d) / Fs;
            iq[2 * i] = (float)(0.4 * Math.Cos(phase));
            iq[(2 * i) + 1] = (float)(0.4 * Math.Sin(phase));
        }

        return iq;
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
