namespace SignalScribe.Capture.Dsp;

/// <summary>Second-order Butterworth section (RBJ cookbook coefficients), 12 dB/octave.</summary>
public sealed class Biquad
{
    private readonly double _b0, _b1, _b2, _a1, _a2;

    private double _x1, _x2, _y1, _y2;

    private Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        _b0 = b0;
        _b1 = b1;
        _b2 = b2;
        _a1 = a1;
        _a2 = a2;
    }

    public static Biquad HighPass(double cutoffHz, double sampleRate)
    {
        var (cos, alpha, a0) = Common(cutoffHz, sampleRate);
        return new Biquad((1 + cos) / 2 / a0, -(1 + cos) / a0, (1 + cos) / 2 / a0, -2 * cos / a0, (1 - alpha) / a0);
    }

    public static Biquad LowPass(double cutoffHz, double sampleRate)
    {
        var (cos, alpha, a0) = Common(cutoffHz, sampleRate);
        return new Biquad((1 - cos) / 2 / a0, (1 - cos) / a0, (1 - cos) / 2 / a0, -2 * cos / a0, (1 - alpha) / a0);
    }

    public float Process(float x)
    {
        var y = (_b0 * x) + (_b1 * _x1) + (_b2 * _x2) - (_a1 * _y1) - (_a2 * _y2);
        _x2 = _x1;
        _x1 = x;
        _y2 = _y1;
        _y1 = y;
        return (float)y;
    }

    private static (double Cos, double Alpha, double A0) Common(double cutoffHz, double sampleRate)
    {
        var w0 = 2 * Math.PI * Math.Clamp(cutoffHz, 1, sampleRate / 2 - 1) / sampleRate;
        var cos = Math.Cos(w0);
        var alpha = Math.Sin(w0) / (2 * (1 / Math.Sqrt(2))); // Q = 1/sqrt(2): Butterworth
        return (cos, alpha, 1 + alpha);
    }
}
