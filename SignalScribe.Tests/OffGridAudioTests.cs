using SignalScribe.Capture.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

/// <summary>
/// Demodulated audio quality against how far the carrier sits from its filterbank bin centre.
///
/// The 12.5 kHz analysis grid does not align with the 5 kHz channel plan, so a channel lands 0,
/// 2.5 or 5 kHz off a bin centre. At 5 kHz the bin clips one side of the FM sideband and the
/// discriminator recovers a badly distorted tone — which is what 147.255 (measured +4924 Hz off
/// the 147.250 bin, the worst case the grid allows) sounds like on air.
/// </summary>
public class OffGridAudioTests(ITestOutputHelper output)
{
    private const double Fs = 3_200_000;
    private const double Spacing = 12_500;
    private const long CenterHz = 146_000_000;

    [Theory]
    [InlineData(0)]      // carrier on the bin centre
    [InlineData(2_500)]  // e.g. 146.940 on the 146.9375 bin
    [InlineData(5_000)]  // e.g. 147.255 on the 147.250 bin — worst case the 12.5 kHz grid allows
    [InlineData(-5_000)]
    public void DistortionRisesSharplyAsTheCarrierLeavesTheBinCentre(double offsetHz)
    {
        const double AudioHz = 1_000, DeviationHz = 3_000;
        var channelizer = new PolyphaseChannelizer(Fs, Spacing);
        var sink = new CaptureSink(channelizer.ChannelCount);
        channelizer.Process(FmTone(8 * Spacing + offsetHz, 1.0, AudioHz, DeviationHz), sink);

        var demod = new NbfmDemodulator(channelizer.OutputSampleRate);
        var pcm = new float[16_000 * 2];
        var n = demod.Process(sink.Samples(sink.PeakBin()), pcm);

        // Skip the settling head, then measure how much of the recovered energy is NOT the 1 kHz tone.
        var seg = pcm[(n / 4)..(n * 3 / 4)];
        var (fundamental, total) = Energy(seg, AudioHz, 16_000);
        var thd = Math.Sqrt(Math.Max(0, total - fundamental) / Math.Max(1e-12, fundamental));
        output.WriteLine($"  offset {offsetHz,7:F0} Hz -> distortion {thd * 100,7:F1} %   (rms {Math.Sqrt(total / seg.Length):F4})");

        // Pin the relationship rather than an exact figure: on-grid is clean, half a bin off is not.
        if (Math.Abs(offsetHz) < 1_000)
        {
            Assert.True(thd < 0.10, $"a centred carrier should demodulate cleanly, got {thd:P1}");
        }
        else if (Math.Abs(offsetHz) >= 5_000)
        {
            Assert.True(thd > 0.30, $"a carrier half a bin off should be visibly distorted, got {thd:P1}");
        }
    }

    /// <summary>Fundamental energy at <paramref name="toneHz"/> via Goertzel, and total AC energy.</summary>
    private static (double Fundamental, double Total) Energy(float[] x, double toneHz, double rate)
    {
        double mean = 0;
        foreach (var v in x) mean += v;
        mean /= x.Length;

        double total = 0, cr = 0, ci = 0;
        var w = 2 * Math.PI * toneHz / rate;
        for (var i = 0; i < x.Length; i++)
        {
            var v = x[i] - mean;
            total += v * v;
            cr += v * Math.Cos(w * i);
            ci += v * Math.Sin(w * i);
        }

        return (2 * (cr * cr + ci * ci) / x.Length, total);
    }

    private static float[] FmTone(double offsetHz, double seconds, double audioHz, double deviationHz)
    {
        var n = (int)(Fs * seconds);
        var iq = new float[n * 2];
        double phase = 0;
        for (var i = 0; i < n; i++)
        {
            phase += 2 * Math.PI * (offsetHz + deviationHz * Math.Sin(2 * Math.PI * audioHz * i / Fs)) / Fs;
            iq[2 * i] = (float)(0.4 * Math.Cos(phase));
            iq[2 * i + 1] = (float)(0.4 * Math.Sin(phase));
        }

        return iq;
    }

    private sealed class CaptureSink(int channels) : IChannelizerSink
    {
        private readonly List<float>[] _buf = [.. Enumerable.Range(0, channels).Select(_ => new List<float>())];
        private readonly double[] _power = new double[channels];

        public void OnHop(ReadOnlySpan<float> frame, long hopIndex)
        {
            for (var c = 0; c < _power.Length; c++)
            {
                _buf[c].Add(frame[2 * c]);
                _buf[c].Add(frame[2 * c + 1]);
                _power[c] += frame[2 * c] * frame[2 * c] + frame[2 * c + 1] * frame[2 * c + 1];
            }
        }

        public int PeakBin()
        {
            var best = 1;
            for (var c = 1; c < _power.Length; c++) if (_power[c] > _power[best]) best = c;
            return best;
        }

        public float[] Samples(int bin) => [.. _buf[bin]];
    }
}
