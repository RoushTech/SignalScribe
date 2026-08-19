using SignalScribe.Capture.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

/// <summary>
/// Whether the 25 kHz → 16 kHz step at the end of the demodulator folds high-frequency content back
/// into the audio.
///
/// The channelizer runs 2× oversampled, so each channel arrives at 25 kHz, and the clip is written
/// at 16 kHz. Downsampling needs everything above the new Nyquist — 8 kHz — gone before the rate
/// changes, or it folds: a component at f lands at |f − 16000|. The demodulator does the rate change
/// by linear interpolation between adjacent samples, which is a very gentle lowpass and no kind of
/// anti-aliasing filter, and the only real filtering in the chain is the 300 Hz high-pass applied
/// *after* the rate change, which cannot undo a fold.
///
/// This matters more on FM than it would elsewhere. Discriminator noise rises with frequency — the
/// triangular spectrum de-emphasis exists to tame — so the 8–12.5 kHz region the fold draws from is
/// the noisiest part of the baseband, and what it folds down is noise rather than signal.
/// </summary>
public class ResamplerAliasingDiag(ITestOutputHelper output)
{
    private const double ChannelRate = 25_000;

    [Fact]
    public void MeasureFoldingOfToneAboveTheOutputNyquist()
    {
        output.WriteLine("  a tone above 8 kHz cannot exist at 16 kHz output; where does it end up,");
        output.WriteLine("  and how loud is it once there? (absolute, so attenuation is visible)");
        output.WriteLine("   tone     lands at     absolute    vs 1 kHz");

        var reference = double.NaN;
        foreach (var toneHz in new[] { 1_000.0, 2_500.0, 3_000.0, 9_000.0, 10_000.0, 11_000.0 })
        {
            var pcm = Demodulate(toneHz);
            var (peakHz, level) = DominantTone(pcm);
            if (double.IsNaN(reference))
            {
                reference = level;
            }

            var fold = toneHz < 8_000 ? toneHz : Math.Abs(16_000 - toneHz);
            output.WriteLine($"  {toneHz,6:F0} Hz {fold,8:F0} Hz {level,11:F1} dB {level - reference,9:F1} dB");
        }

        output.WriteLine(string.Empty);
        output.WriteLine("  A tone at 9-11 kHz appearing at 5-7 kHz is the fold: content that should");
        output.WriteLine("  have been filtered out before the rate change instead lands in the voice band.");
    }

    /// <summary>FM modulated by a single tone, straight into the demodulator at the channel rate.</summary>
    private static float[] Demodulate(double toneHz)
    {
        const int Samples = (int)(ChannelRate * 2);
        var iq = new float[Samples * 2];
        double phase = 0;
        for (var i = 0; i < Samples; i++)
        {
            var t = i / ChannelRate;
            // Modest deviation so the tone stays inside the channel and nothing else is under test.
            var deviation = 2_000 * Math.Sin(2 * Math.PI * toneHz * t);
            phase += 2 * Math.PI * deviation / ChannelRate;
            iq[2 * i] = (float)(0.4 * Math.Cos(phase));
            iq[(2 * i) + 1] = (float)(0.4 * Math.Sin(phase));
        }

        var demod = new NbfmDemodulator(ChannelRate);
        var pcm = new float[NbfmDemodulator.OutputSampleRate * 3];
        var written = demod.Process(iq, pcm);
        return pcm[..written];
    }

    /// <summary>Strongest spectral peak in the 16 kHz output, and its level relative to total power.</summary>
    private static (double Hz, double Db) DominantTone(float[] pcm)
    {
        const int N = 8192;
        if (pcm.Length < N)
        {
            return (0, double.NegativeInfinity);
        }

        // Skip the first half second: the DC tracker and de-emphasis are still settling.
        var start = Math.Min(pcm.Length - N, NbfmDemodulator.OutputSampleRate / 2);
        var re = new double[N];
        var im = new double[N];
        for (var i = 0; i < N; i++)
        {
            var w = 0.5 - (0.5 * Math.Cos(2 * Math.PI * i / N));
            re[i] = pcm[start + i] * w;
        }

        Fft(re, im);

        double best = 0;
        var bestBin = 0;
        for (var k = 1; k < N / 2; k++)
        {
            var mag = (re[k] * re[k]) + (im[k] * im[k]);
            if (mag > best)
            {
                best = mag;
                bestBin = k;
            }
        }

        // Absolute, normalised by the window length: a relative figure cannot show attenuation,
        // because a folded tone that is the only thing present stays 100% of the power however
        // far it has been knocked down.
        var binHz = NbfmDemodulator.OutputSampleRate / (double)N;
        return (bestBin * binHz, 10 * Math.Log10(Math.Max(best, 1e-30) / (N * (double)N / 4)));
    }

    private static void Fft(double[] re, double[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2 * Math.PI / len;
            var wRe = Math.Cos(ang);
            var wIm = Math.Sin(ang);
            for (var i = 0; i < n; i += len)
            {
                double curRe = 1, curIm = 0;
                for (var j = 0; j < len / 2; j++)
                {
                    var uRe = re[i + j];
                    var uIm = im[i + j];
                    var vRe = (re[i + j + (len / 2)] * curRe) - (im[i + j + (len / 2)] * curIm);
                    var vIm = (re[i + j + (len / 2)] * curIm) + (im[i + j + (len / 2)] * curRe);
                    re[i + j] = uRe + vRe;
                    im[i + j] = uIm + vIm;
                    re[i + j + (len / 2)] = uRe - vRe;
                    im[i + j + (len / 2)] = uIm - vIm;
                    var nextRe = (curRe * wRe) - (curIm * wIm);
                    curIm = (curRe * wIm) + (curIm * wRe);
                    curRe = nextRe;
                }
            }
        }
    }
}
