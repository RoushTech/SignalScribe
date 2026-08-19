namespace SignalScribe.Tests;

/// <summary>
/// Synthetic digital modulation for tests: symbol streams shaped the way a real transmitter shapes
/// them, at the deviation plans the real modes use.
///
/// The pulse shaping is load-bearing, not decoration. A one-pole smoother — the obvious shortcut —
/// never quite arrives: at 25 kSPS a 4800-baud symbol lasts 5.2 samples, so the waveform reaches only
/// ~95% of its level before the next symbol pulls it away, and every measured level lands ~100 Hz low.
/// That is most of the distance between DMR's deviation plan and P25's, and it would have tests
/// failing signals the code reads perfectly well. A raised cosine satisfies the Nyquist criterion, so
/// the waveform passes exactly through each symbol level at the symbol instant — which is what a real
/// receiver sees after matched filtering.
/// </summary>
internal static class DigitalSignals
{
    /// <summary>DMR's four-level plan: outer ±1944 Hz, inner ±648 Hz, 4800 symbols per second.</summary>
    public const double DmrOuterHz = 1_944, DmrInnerHz = 648, C4fmBaud = 4_800;

    /// <summary>P25 Phase 1, YSF and NXDN96 all share this plan, which is why levels alone cannot separate them.</summary>
    public const double NarrowOuterHz = 1_800, NarrowInnerHz = 600;

    /// <summary>Four-level C4FM deviation, in Hz, at <paramref name="sampleRate"/>.</summary>
    public static float[] C4fm(double sampleRate, double seconds, double outer, double inner, double baud, int seed)
    {
        double[] levels = [outer, inner, -inner, -outer];
        return Shaped(sampleRate, seconds, baud, seed, rolloff: 0.2, rng => levels[rng.Next(levels.Length)], out _);
    }

    /// <summary>
    /// As <see cref="C4fm"/>, but also returns the symbols that were sent. Only meaningful for a
    /// probe: knowing the truth is what lets a symbol error rate be measured at all.
    /// </summary>
    public static float[] C4fmWithTruth(double sampleRate, double seconds, double outer, double inner, double baud, int seed, out double[] symbols)
    {
        double[] levels = [outer, inner, -inner, -outer];
        return Shaped(sampleRate, seconds, baud, seed, rolloff: 0.2, rng => levels[rng.Next(levels.Length)], out symbols);
    }

    /// <summary>Two-level FSK with the transmitted symbols, for measuring error rates.</summary>
    public static float[] FskWithTruth(double sampleRate, double seconds, double deviation, double baud, int seed, double rolloff, out double[] symbols)
        => Shaped(sampleRate, seconds, baud, seed, rolloff, rng => rng.Next(2) == 0 ? deviation : -deviation, out symbols);

    /// <summary>Two-level FSK deviation. A higher <paramref name="rolloff"/> gives the gentler transitions of GMSK.</summary>
    public static float[] Fsk(double sampleRate, double seconds, double deviation, double baud, int seed, double rolloff)
        => Shaped(sampleRate, seconds, baud, seed, rolloff, rng => rng.Next(2) == 0 ? deviation : -deviation, out _);

    /// <summary>Bell 202 AFSK: an audio sine switching between 1200 and 2200 Hz once per bit.</summary>
    public static float[] Afsk(double sampleRate, double seconds, double deviation, int seed)
    {
        var rng = new Random(seed);
        var n = (int)(sampleRate * seconds);
        var samplesPerBit = sampleRate / 1_200;
        var result = new float[n];

        double phase = 0, nextBitAt = 0;
        var toneHz = 1_200.0;
        for (var i = 0; i < n; i++)
        {
            if (i >= nextBitAt)
            {
                toneHz = rng.Next(2) == 0 ? 1_200.0 : 2_200.0;
                nextBitAt += samplesPerBit;
            }

            phase += 2 * Math.PI * toneHz / sampleRate;
            result[i] = (float)(deviation * Math.Sin(phase));
        }

        return result;
    }

    /// <summary>Impulses at the symbol rate convolved with a raised cosine.</summary>
    /// <summary>
    /// C4FM from a scripted symbol sequence rather than random draws — for embedding real sync
    /// patterns (DMR's, say) in an otherwise random stream. Loops if the script runs short.
    /// </summary>
    public static float[] C4fmScripted(double sampleRate, double seconds, double baud, IReadOnlyList<double> script)
    {
        var cursor = 0;
        return Shaped(sampleRate, seconds, baud, seed: 1, rolloff: 0.2, _ => script[cursor++ % script.Count], out _);
    }

    private static float[] Shaped(double sampleRate, double seconds, double baud, int seed, double rolloff, Func<Random, double> pick, out double[] sent)
    {
        var rng = new Random(seed);
        var n = (int)(sampleRate * seconds);
        var samplesPerSymbol = sampleRate / baud;
        var symbolCount = (int)(n / samplesPerSymbol) + 8;

        var symbols = new double[symbolCount];
        for (var k = 0; k < symbolCount; k++)
        {
            symbols[k] = pick(rng);
        }

        sent = symbols;

        const int Span = 4; // symbols either side — the tails beyond this are negligible
        var result = new float[n];
        for (var i = 0; i < n; i++)
        {
            var centre = i / samplesPerSymbol;
            var first = Math.Max(0, (int)Math.Floor(centre) - Span);
            var last = Math.Min(symbolCount - 1, (int)Math.Ceiling(centre) + Span);

            double sum = 0;
            for (var k = first; k <= last; k++)
            {
                sum += symbols[k] * RaisedCosine(centre - k, rolloff);
            }

            result[i] = (float)sum;
        }

        return result;
    }

    /// <summary>Raised-cosine pulse, argument in symbol periods. Unity at zero, zero at every other symbol instant.</summary>
    private static double RaisedCosine(double u, double beta)
    {
        if (Math.Abs(u) < 1e-9)
        {
            return 1.0;
        }

        var scaled = 2 * beta * u;
        var denominator = 1 - (scaled * scaled);
        if (Math.Abs(denominator) < 1e-9)
        {
            return Math.PI / 4 * Sinc(1 / (2 * beta)); // removable singularity at u = ±1/(2β)
        }

        return Sinc(u) * Math.Cos(Math.PI * beta * u) / denominator;
    }

    private static double Sinc(double u) => Math.Abs(u) < 1e-9 ? 1.0 : Math.Sin(Math.PI * u) / (Math.PI * u);
}
