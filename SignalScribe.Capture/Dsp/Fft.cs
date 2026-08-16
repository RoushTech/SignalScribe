namespace SignalScribe.Capture.Dsp;

/// <summary>
/// Iterative in-place radix-2 complex FFT with precomputed twiddles and bit-reversal table.
/// Zero allocations per transform (DSP hot-path rule). Sufficient for spectrum display; the PFB
/// channelizer (milestone 1) will bring its own optimized transform.
/// </summary>
public sealed class Fft
{
    private readonly int _n;

    private readonly float[] _cos;

    private readonly float[] _sin;

    private readonly int[] _bitrev;

    public Fft(int n)
    {
        if ((n & (n - 1)) != 0 || n < 2)
        {
            throw new ArgumentException("FFT size must be a power of two", nameof(n));
        }

        _n = n;
        _cos = new float[n / 2];
        _sin = new float[n / 2];
        for (var i = 0; i < n / 2; i++)
        {
            _cos[i] = MathF.Cos(-2 * MathF.PI * i / n);
            _sin[i] = MathF.Sin(-2 * MathF.PI * i / n);
        }

        _bitrev = new int[n];
        var bits = int.TrailingZeroCount(n);
        for (var i = 0; i < n; i++)
        {
            var r = 0;
            for (var b = 0; b < bits; b++)
            {
                r = (r << 1) | ((i >> b) & 1);
            }

            _bitrev[i] = r;
        }
    }

    /// <summary>In-place FFT over interleaved complex data (re, im, re, im, …) of length 2*N floats.</summary>
    public void Transform(Span<float> interleaved)
    {
        if (interleaved.Length != _n * 2)
        {
            throw new ArgumentException($"Expected {_n * 2} floats", nameof(interleaved));
        }

        for (var i = 0; i < _n; i++)
        {
            var j = _bitrev[i];
            if (j > i)
            {
                (interleaved[2 * i], interleaved[2 * j]) = (interleaved[2 * j], interleaved[2 * i]);
                (interleaved[2 * i + 1], interleaved[2 * j + 1]) = (interleaved[2 * j + 1], interleaved[2 * i + 1]);
            }
        }

        for (var len = 2; len <= _n; len <<= 1)
        {
            var half = len >> 1;
            var step = _n / len;
            for (var start = 0; start < _n; start += len)
            {
                for (var k = 0; k < half; k++)
                {
                    var wRe = _cos[k * step];
                    var wIm = _sin[k * step];
                    var even = (start + k) * 2;
                    var odd = (start + k + half) * 2;
                    var oRe = interleaved[odd] * wRe - interleaved[odd + 1] * wIm;
                    var oIm = interleaved[odd] * wIm + interleaved[odd + 1] * wRe;
                    interleaved[odd] = interleaved[even] - oRe;
                    interleaved[odd + 1] = interleaved[even + 1] - oIm;
                    interleaved[even] += oRe;
                    interleaved[even + 1] += oIm;
                }
            }
        }
    }
}
