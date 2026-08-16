using SignalScribe.Capture.Dsp;
using Xunit;

namespace SignalScribe.Tests;

public class FftTests
{
    [Fact]
    public void ImpulseTransformsToFlatSpectrum()
    {
        var fft = new Fft(8);
        var data = new float[16];
        data[0] = 1f; // delta at n=0 → all bins = 1 + 0i

        fft.Transform(data);

        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(1f, data[2 * i], 3);
            Assert.Equal(0f, data[2 * i + 1], 3);
        }
    }

    [Fact]
    public void ComplexToneLandsInSingleBin()
    {
        const int n = 64;
        const int bin = 5;
        var fft = new Fft(n);
        var data = new float[n * 2];
        for (var i = 0; i < n; i++)
        {
            data[2 * i] = MathF.Cos(2 * MathF.PI * bin * i / n);
            data[2 * i + 1] = MathF.Sin(2 * MathF.PI * bin * i / n);
        }

        fft.Transform(data);

        for (var i = 0; i < n; i++)
        {
            var mag = MathF.Sqrt(data[2 * i] * data[2 * i] + data[2 * i + 1] * data[2 * i + 1]);
            Assert.Equal(i == bin ? n : 0f, mag, 2);
        }
    }

    [Fact]
    public void AnalyzerPutsToneAtCorrectRowPosition()
    {
        // +100 kHz tone at 1 MSPS with 1024 bins → offset +102.4 bins from row center (bin 512).
        const double fs = 1_000_000;
        const double toneHz = 100_000;
        var analyzer = new SpectrumAnalyzer(fftSize: 1024, averageFrames: 4);
        var iq = new float[1024 * 2 * 4];
        for (var i = 0; i < iq.Length / 2; i++)
        {
            var phase = 2 * Math.PI * toneHz * i / fs;
            iq[2 * i] = (float)(0.5 * Math.Cos(phase));
            iq[2 * i + 1] = (float)(0.5 * Math.Sin(phase));
        }

        var row = analyzer.Feed(iq, 146_000_000, (long)fs);

        Assert.NotNull(row);
        var peak = 0;
        for (var i = 1; i < row.Bins.Length; i++)
        {
            if (row.Bins[i] > row.Bins[peak])
            {
                peak = i;
            }
        }

        var expected = 512 + (int)Math.Round(toneHz / fs * 1024);
        Assert.InRange(peak, expected - 1, expected + 1);
    }
}
