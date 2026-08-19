using SignalScribe.Capture.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

/// <summary>
/// Does 4800-baud symbol information survive the existing channelizer when the carrier sits off its
/// bin? This decides whether digital voice framing needs per-channel fine DDC first — a change to the
/// prototype filter that every existing feature depends on — or whether it can be built on the
/// discriminator the way the packet decoder was.
///
/// Timing is *ideal* here: symbols are read at their known instants rather than recovered. That is
/// deliberate. The question is whether the information is still present after the channel, which is
/// separate from whether a synchroniser can find it, and answering them together would leave a
/// failure ambiguous. Prints, does not assert.
///
/// Measured answer, 2026-08-17 — <b>fine DDC is not needed</b>:
/// <code>
///   offset        D-STAR (2-level)      DMR (4-level)
///   0 Hz          0.00%  eye 0.99       0.00%  eye 0.96
///   ±2500 Hz      0.00%  eye 0.97       0.00%  eye 0.91
///   ±5000 Hz      0.00%  eye 0.95       0.39%  eye 0.85
/// </code>
/// ±5000 Hz is the worst case the 12.5 kHz analysis grid allows against the 5 kHz channel plan, and
/// even there the raw symbol error rate is well inside what these modes' FEC is built to absorb. The
/// channelizer is therefore not the limiting factor for digital voice framing, and rewriting its
/// prototype filter — which every existing feature depends on — buys nothing for this work.
///
/// Caveat worth keeping in view: these bursts are noiseless and from an ideal transmitter. What the
/// probe rules out is the *channelizer* destroying symbols; it says nothing about sensitivity on a
/// weak signal, which only real air will settle.
/// </summary>
public class SymbolRecoveryProbe(ITestOutputHelper output)
{
    private const double Fs = 3_200_000;
    private const double Spacing = 12_500;
    private const double Baud = 4_800;

    [Fact]
    public void FourLevelSymbolErrorRateAgainstCarrierOffset()
    {
        output.WriteLine("DMR C4FM, +/-1944/648 Hz, 4800 baud — symbol errors with ideal timing");
        foreach (var offset in new[] { 0.0, 1_250, 2_500, 3_750, 5_000, -2_500, -5_000 })
        {
            var sent = Probe(offset, fourLevel: true, out var recovered, out var eye);
            output.WriteLine($"  offset {offset,6:F0} Hz -> {sent:P2} symbol errors, eye {eye:F2}  ({recovered})");
        }
    }

    [Fact]
    public void TwoLevelSymbolErrorRateAgainstCarrierOffset()
    {
        output.WriteLine("D-STAR GMSK, +/-1200 Hz, 4800 baud — symbol errors with ideal timing");
        foreach (var offset in new[] { 0.0, 1_250, 2_500, 3_750, 5_000, -2_500, -5_000 })
        {
            var sent = Probe(offset, fourLevel: false, out var recovered, out var eye);
            output.WriteLine($"  offset {offset,6:F0} Hz -> {sent:P2} symbol errors, eye {eye:F2}  ({recovered})");
        }
    }

    /// <summary>
    /// Runs a burst through the real channelizer and discriminator, samples at the known symbol
    /// instants, slices to the nearest nominal level, and returns the fraction that came out wrong.
    /// </summary>
    private static double Probe(double offsetHz, bool fourLevel, out string detail, out double eyeOpening)
    {
        const double Seconds = 0.5;
        double[] truth;
        var deviation = fourLevel
            ? DigitalSignals.C4fmWithTruth(25_000, Seconds, DigitalSignals.DmrOuterHz, DigitalSignals.DmrInnerHz, Baud, seed: 21, out truth)
            : DigitalSignals.FskWithTruth(25_000, Seconds, 1_200, Baud, seed: 21, rolloff: 0.5, out truth);

        var channelizer = new PolyphaseChannelizer(Fs, Spacing);
        var sink = new ProbeSink(channelizer.ChannelCount);
        channelizer.Process(Modulate(deviation, (32 * Spacing) + offsetHz), sink);

        var bin = sink.Samples(sink.PeakBin());
        var rate = channelizer.OutputSampleRate;
        var discriminated = Discriminate(bin, rate);

        // Remove the carrier offset the same way the demodulator does. The amplitude scale is set per
        // candidate phase below rather than once here: it has to be measured *at the symbol instants*,
        // because the pulse shaping overshoots between them and a scale taken across all samples
        // reads high. Two-level slicing barely cares — it is a sign decision — but with four levels a
        // 20% scale error moves every inner symbol across a boundary, which is worth getting right.
        var mean = discriminated.Average();

        double[] levels = fourLevel
            ? [DigitalSignals.DmrOuterHz, DigitalSignals.DmrInnerHz, -DigitalSignals.DmrInnerHz, -DigitalSignals.DmrOuterHz]
            : [1_200, -1_200];

        var expectedRms = Math.Sqrt(levels.Sum(l => l * l) / levels.Length);

        var samplesPerSymbol = rate / Baud;

        // Find the sampling instant rather than assuming it. The channelizer's prototype filter is
        // eight taps per branch and the WOLA structure adds more, so the discriminator output lags the
        // modulator by tens of samples — far more than one symbol. A real receiver recovers this with
        // a timing loop; the probe just searches for the best alignment, because the question here is
        // whether the eye is open at *any* phase, not whether a particular guess was lucky.
        //
        // Scored on eye margin, which needs no knowledge of what was sent — so the search cannot
        // quietly cheat by fitting the answer.
        var bestDelay = 0.0;
        var bestMargin = -1.0;
        var bestScale = 1.0;
        for (var delay = 0.0; delay < 8 * samplesPerSymbol; delay += 0.1)
        {
            var scale = ScaleAt(discriminated, mean, samplesPerSymbol, delay, truth.Length, expectedRms);
            var margin = MeanMargin(discriminated, levels, mean, scale, samplesPerSymbol, delay, truth.Length);
            if (margin > bestMargin)
            {
                bestMargin = margin;
                bestDelay = delay;
                bestScale = scale;
            }
        }

        // The eye-margin search fixes the sampling *phase* but cannot fix which symbol index that
        // phase corresponds to: a two-level eye looks equally open a whole symbol early or late, and
        // the search happily settles one symbol over. Comparing against the truth array then reports
        // chance errors on a signal that was recovered perfectly. A real receiver resolves this with
        // a frame sync pattern, which is precisely the framers' job; here it is enough to allow the
        // constant symbol offset and report the best alignment.
        var bestErrors = int.MaxValue;
        var bestCounted = 0;
        var bestShift = 0;

        for (var shift = -2; shift <= 2; shift++)
        {
            const int SkipSymbols = 40;
            int errors = 0, counted = 0;
            for (var k = SkipSymbols; k < truth.Length - SkipSymbols; k++)
            {
                var at = ((k + shift) * samplesPerSymbol) + bestDelay;
                if (at < 0 || at + 1 >= discriminated.Length)
                {
                    continue;
                }

                var value = (Interpolate(discriminated, at) - mean) * bestScale;
                if (Math.Abs(Nearest(levels, value) - truth[k]) > 1)
                {
                    errors++;
                }

                counted++;
            }

            if (counted > 0 && errors < bestErrors)
            {
                bestErrors = errors;
                bestCounted = counted;
                bestShift = shift;
            }
        }

        eyeOpening = bestMargin;
        detail = $"{bestErrors}/{bestCounted}, delay {bestDelay:F1} samples, shift {bestShift}";
        return bestCounted == 0 ? 1 : bestErrors / (double)bestCounted;
    }

    /// <summary>
    /// AGC factor for a given sampling phase: scales the symbol-instant samples so their RMS matches
    /// what the level plan implies. Measured at the instants, not across the waveform, because the
    /// pulse shaping overshoots in between.
    /// </summary>
    private static double ScaleAt(float[] samples, double mean, double samplesPerSymbol, double delay, int symbolCount, double expectedRms)
    {
        const int Skip = 40;
        double sum = 0;
        var counted = 0;
        for (var k = Skip; k < symbolCount - Skip; k++)
        {
            var at = (k * samplesPerSymbol) + delay;
            if (at + 1 >= samples.Length)
            {
                break;
            }

            var v = Interpolate(samples, at) - mean;
            sum += v * v;
            counted++;
        }

        if (counted == 0)
        {
            return 1;
        }

        var rms = Math.Sqrt(sum / counted);
        return rms > 1e-9 ? expectedRms / rms : 1;
    }

    /// <summary>Mean distance to a decision boundary at a given sampling phase — the eye opening, 0 to 1.</summary>
    private static double MeanMargin(float[] samples, double[] levels, double mean, double scale, double samplesPerSymbol, double delay, int symbolCount)
    {
        const int Skip = 40;
        double total = 0;
        var counted = 0;
        for (var k = Skip; k < symbolCount - Skip; k++)
        {
            var at = (k * samplesPerSymbol) + delay;
            if (at + 1 >= samples.Length)
            {
                break;
            }

            total += Margin(levels, (Interpolate(samples, at) - mean) * scale);
            counted++;
        }

        return counted == 0 ? 0 : total / counted;
    }

    private static double Interpolate(float[] samples, double at)
    {
        var index = (int)at;
        var frac = at - index;
        return samples[index] + ((samples[index + 1] - samples[index]) * frac);
    }

    private static double Nearest(double[] levels, double value)
    {
        var best = levels[0];
        foreach (var level in levels)
        {
            if (Math.Abs(level - value) < Math.Abs(best - value))
            {
                best = level;
            }
        }

        return best;
    }

    private static double Margin(double[] levels, double value)
    {
        var sorted = levels.OrderBy(v => v).ToArray();
        var smallestGap = double.MaxValue;
        for (var i = 0; i + 1 < sorted.Length; i++)
        {
            smallestGap = Math.Min(smallestGap, sorted[i + 1] - sorted[i]);
        }

        var nearest = Nearest(levels, value);
        var distance = double.MaxValue;
        foreach (var level in levels)
        {
            if (level != nearest)
            {
                distance = Math.Min(distance, Math.Abs(value - ((level + nearest) / 2)));
            }
        }

        return Math.Min(1, distance / (smallestGap / 2));
    }

    private static float[] Discriminate(float[] iq, double rate)
    {
        var outBuf = new float[(iq.Length / 2) - 1];
        float prevI = iq[0], prevQ = iq[1];
        var scale = rate / (2 * Math.PI);
        var n = 0;
        for (var s = 2; s + 1 < iq.Length; s += 2)
        {
            var i = iq[s];
            var q = iq[s + 1];
            outBuf[n++] = (float)(Math.Atan2((prevI * q) - (prevQ * i), (prevI * i) + (prevQ * q)) * scale);
            prevI = i;
            prevQ = q;
        }

        return outBuf[..n];
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

    private sealed class ProbeSink(int channels) : IChannelizerSink
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
