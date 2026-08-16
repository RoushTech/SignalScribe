using SignalScribe.Capture.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

/// <summary>
/// The reported channel frequency comes from bin centre + measured carrier offset, so a bias in
/// that measurement puts transmissions on the wrong channel. These pin the estimator's accuracy
/// across the modulation shapes 2m actually carries — narrow voice through wideband packet whose
/// sidebands run past the bin edge — because a real 144.390 APRS signal was reporting ~600 Hz of
/// offset instead of 2500, and filter truncation was the obvious suspect. It is not: the estimate
/// is unbiased at every bandwidth here, which is what makes an off-frequency *transmitter* (or
/// receiver) the remaining explanation rather than a DSP fault.
/// </summary>
public class CarrierOffsetBiasTests(ITestOutputHelper output)
{
    private const double Fs = 3_200_000;
    private const double Spacing = 12_500;
    private const long CenterHz = 146_000_000;

    [Theory]
    // tone, deviation, label — what the modulation looks like at the antenna
    [InlineData(1200, 3000, "1200 Hz tone, +/-3k (my earlier synthetic)")]
    [InlineData(2200, 3000, "2200 Hz mark tone, +/-3k (real APRS upper tone)")]
    [InlineData(2200, 5000, "2200 Hz, +/-5k (over-deviating packet)")]
    [InlineData(1000, 1500, "1000 Hz, +/-1.5k (narrow, voice-like)")]
    [InlineData(300, 5000, "300 Hz, +/-5k (voice: low audio, wide deviation)")]
    public void OffsetEstimateIsUnbiasedRegardlessOfOccupiedBandwidth(double toneHz, double deviationHz, string label)
    {
        const double TrueOffset = 2500;    // 144.390 sitting on the 144.3875 bin
        var channelizer = new PolyphaseChannelizer(Fs, Spacing);
        var sink = new CaptureSink(channelizer.ChannelCount);
        channelizer.Process(Modulated(8 * Spacing + TrueOffset, 1.0, toneHz, deviationHz), sink);

        var bin = sink.PeakBin();
        var demod = new NbfmDemodulator(channelizer.OutputSampleRate);
        demod.Process(sink.Samples(bin), new float[16_000 * 2]);

        var carson = 2 * (deviationHz + toneHz);
        output.WriteLine(
            $"{label,-46} | Carson {carson / 1000,4:F1} kHz, upper edge "
            + $"+{(TrueOffset + carson / 2) / 1000:F1} kHz vs bin half-width 6.25 kHz "
            + $"| measured {demod.AverageOffsetHz,7:F0} Hz of {TrueOffset:F0}");

        // Within a tenth of the 2.5 kHz channel step: comfortably enough to snap to the right channel.
        Assert.InRange(demod.AverageOffsetHz, TrueOffset - 250, TrueOffset + 250);
        Assert.Equal(144_390_000, Snap(demod.AverageOffsetHz));
    }

    private static long Snap(double offset) => ChannelGrid.Snap(144_387_500, offset);

    private static float[] Modulated(double offsetHz, double seconds, double toneHz, double deviationHz)
    {
        var n = (int)(Fs * seconds);
        var iq = new float[n * 2];
        var rng = new Random(9);
        double phase = 0;
        for (var i = 0; i < n; i++)
        {
            var mod = Math.Sin(2 * Math.PI * toneHz * i / Fs);
            phase += 2 * Math.PI * (offsetHz + deviationHz * mod) / Fs;
            iq[2 * i] = (float)(0.4 * Math.Cos(phase) + (rng.NextDouble() * 2 - 1) * 0.008);
            iq[2 * i + 1] = (float)(0.4 * Math.Sin(phase) + (rng.NextDouble() * 2 - 1) * 0.008);
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
            for (var c = 1; c < _power.Length; c++)
            {
                if (_power[c] > _power[best]) best = c;
            }
            return best;
        }

        public float[] Samples(int bin) => [.. _buf[bin]];
    }
}
