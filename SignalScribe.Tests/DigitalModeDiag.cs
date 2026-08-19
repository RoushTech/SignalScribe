using SignalScribe.Capture.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

/// <summary>
/// Diagnostic: what the mode classifier sees on ideal deviation versus the same signal recovered
/// through the real channelizer and discriminator. Prints, does not assert.
/// </summary>
public class DigitalModeDiag(ITestOutputHelper output)
{
    private const double Fs = 3_200_000;
    private const double Spacing = 12_500;

    [Fact]
    public void CompareIdealAndRecoveredLevels()
    {
        var ideal = DigitalSignals.C4fm(25_000, 2.0, DigitalSignals.DmrOuterHz, DigitalSignals.DmrInnerHz, DigitalSignals.C4fmBaud, seed: 1);

        var direct = new ModeClassifier(25_000);
        foreach (var s in ideal)
        {
            direct.Feed(s);
        }

        output.WriteLine($"ideal deviation   -> {direct.Classify()}  {direct.LastScore}");

        foreach (var offset in new[] { 0.0, 2_500.0 })
        {
            var channelizer = new PolyphaseChannelizer(Fs, Spacing);
            var sink = new BinSink(channelizer.ChannelCount);
            channelizer.Process(Modulate(ideal, (8 * Spacing) + offset), sink);

            var demod = new NbfmDemodulator(channelizer.OutputSampleRate);
            var pcm = new float[16_000 * 3];
            demod.Process(sink.Samples(sink.PeakBin()), pcm);

            output.WriteLine($"through chain {offset,6:F0} Hz -> {demod.Mode}  {demod.ModeScore}");
        }
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

    private sealed class BinSink(int channels) : IChannelizerSink
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
