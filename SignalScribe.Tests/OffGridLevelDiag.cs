using SignalScribe.Capture.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

/// <summary>
/// What a channel pays, in level, for sitting off the analysis grid.
///
/// Squelch compares a bin's power against the band median, so anything that costs a channel level
/// costs it sensitivity directly. A 12.5 kHz filterbank bin is 6.25 kHz wide either side of centre,
/// and an NBFM carrier swings ±5 kHz — so a carrier parked 2.5 kHz off centre puts its upper
/// excursions 7.5 kHz out, past the filter edge, and the energy that falls outside is simply gone
/// from the bin the gate is watching.
///
/// This measures the penalty rather than arguing about it, because several real channels sit off
/// grid (146.790 and 147.180 by 2.5 kHz, 144.920 by 5 kHz) and it explains why the weak ones open
/// less readily than their signal strength alone suggests.
/// </summary>
public class OffGridLevelDiag(ITestOutputHelper output)
{
    private const double Fs = 3_200_000;
    private const double Spacing = 12_500;

    [Fact]
    public void MeasureLevelLossVersusCarrierOffset()
    {
        var reference = double.NaN;

        output.WriteLine("  offset   peak bin power   loss");
        foreach (var offset in new[] { 0.0, 1_250.0, 2_500.0, 3_750.0, 5_000.0, 6_250.0 })
        {
            var power = PeakBinPower(offset);
            var db = 10 * Math.Log10(power);
            if (double.IsNaN(reference))
            {
                reference = db;
            }

            output.WriteLine($"  {offset,6:F0} Hz {db,10:F2} dB {db - reference,7:F2} dB");
        }

        output.WriteLine(string.Empty);
        output.WriteLine("  146.790 and 147.180 sit 2500 Hz off; 144.920 sits 5000 Hz off.");
    }

    private static double PeakBinPower(double offsetHz)
    {
        var channelizer = new PolyphaseChannelizer(Fs, Spacing);
        var sink = new PowerSink(channelizer.ChannelCount);
        channelizer.Process(Modulate(offsetHz, seconds: 0.4), sink);
        return sink.Peak();
    }

    /// <summary>An NBFM carrier deviated ±5 kHz by a 1 kHz tone — an ordinary voice channel's spectrum.</summary>
    private static float[] Modulate(double offsetHz, double seconds)
    {
        var n = (int)(Fs * seconds);
        var iq = new float[n * 2];
        double phase = 0;
        for (var i = 0; i < n; i++)
        {
            var t = i / Fs;
            var deviation = 5_000 * Math.Sin(2 * Math.PI * 1_000 * t);
            phase += 2 * Math.PI * ((32 * Spacing) + offsetHz + deviation) / Fs;
            iq[2 * i] = (float)(0.4 * Math.Cos(phase));
            iq[(2 * i) + 1] = (float)(0.4 * Math.Sin(phase));
        }

        return iq;
    }

    private sealed class PowerSink(int channels) : IChannelizerSink
    {
        private readonly double[] _power = new double[channels];

        private long _frames;

        public void OnHop(ReadOnlySpan<float> frame, long hopIndex)
        {
            _frames++;
            for (var c = 0; c < _power.Length; c++)
            {
                _power[c] += (frame[2 * c] * frame[2 * c]) + (frame[(2 * c) + 1] * frame[(2 * c) + 1]);
            }
        }

        public double Peak()
        {
            var best = 0.0;
            foreach (var p in _power)
            {
                best = Math.Max(best, p);
            }

            return best / Math.Max(1, _frames);
        }
    }
}
