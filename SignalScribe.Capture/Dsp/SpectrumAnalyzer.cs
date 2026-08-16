using SignalScribe.Contracts;

namespace SignalScribe.Capture.Dsp;

/// <summary>
/// Wideband power spectrum for the UI waterfall: Hann-windowed FFT frames, power-averaged, then
/// quantized to bytes over a fixed dB range and fftshifted so the center frequency sits mid-row.
/// All buffers preallocated; Feed() allocates only when a row is emitted (the row's byte[]).
/// </summary>
public sealed class SpectrumAnalyzer(int fftSize = 1024, int averageFrames = 24)
{
    public const double FloorDb = -110;

    public const double CeilDb = -20;

    private readonly Fft _fft = new(fftSize);

    private readonly float[] _window = BuildHann(fftSize);

    private readonly float[] _frame = new float[fftSize * 2];

    private readonly double[] _powerAccum = new double[fftSize];

    private int _frameFill;

    private int _framesAccumulated;

    public int FftSize { get; } = fftSize;

    /// <summary>Feeds interleaved IQ; returns a finished row (and resets accumulation) or null.</summary>
    public SpectrumRow? Feed(ReadOnlySpan<float> iq, long centerFrequencyHz, long spanHz)
    {
        var consumed = 0;
        while (consumed < iq.Length)
        {
            var need = _frame.Length - _frameFill;
            var take = Math.Min(need, iq.Length - consumed);
            iq.Slice(consumed, take).CopyTo(_frame.AsSpan(_frameFill));
            _frameFill += take;
            consumed += take;

            if (_frameFill < _frame.Length)
            {
                break;
            }

            _frameFill = 0;
            for (var i = 0; i < FftSize; i++)
            {
                _frame[2 * i] *= _window[i];
                _frame[2 * i + 1] *= _window[i];
            }

            _fft.Transform(_frame);
            for (var i = 0; i < FftSize; i++)
            {
                var re = _frame[2 * i];
                var im = _frame[2 * i + 1];
                _powerAccum[i] += re * re + im * im;
            }

            if (++_framesAccumulated >= averageFrames)
            {
                var row = EmitRow(centerFrequencyHz, spanHz);
                _framesAccumulated = 0;
                Array.Clear(_powerAccum);
                return row;
            }
        }

        return null;
    }

    private SpectrumRow EmitRow(long centerFrequencyHz, long spanHz)
    {
        var bins = new byte[FftSize];
        var scale = 1.0 / (averageFrames * (double)FftSize * FftSize);
        for (var i = 0; i < FftSize; i++)
        {
            // fftshift: bin 0 (DC) belongs at the row center.
            var src = (i + FftSize / 2) % FftSize;
            var db = 10 * Math.Log10(_powerAccum[src] * scale + 1e-20);
            bins[i] = (byte)Math.Clamp((db - FloorDb) / (CeilDb - FloorDb) * 255, 0, 255);
        }

        return new SpectrumRow(centerFrequencyHz, spanHz, DateTime.UtcNow, FloorDb, CeilDb, bins);
    }

    private static float[] BuildHann(int n)
    {
        var w = new float[n];
        for (var i = 0; i < n; i++)
        {
            w[i] = 0.5f * (1 - MathF.Cos(2 * MathF.PI * i / n));
        }

        return w;
    }
}
