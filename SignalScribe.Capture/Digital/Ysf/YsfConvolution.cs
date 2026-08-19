namespace SignalScribe.Capture.Digital.Ysf;

/// <summary>
/// The rate-1/2, constraint-length-5 convolutional code that protects Fusion's FICH, decoded with a
/// Viterbi traceback.
///
/// One dibit off the air carries the code's two output bits, so 100 dibits decode to 100 input bits
/// — 96 of payload plus the four zeros that flush the register. Sixteen states and a hundred steps
/// is small enough that trying several parameter sets per frame costs nothing measurable, which is
/// what <see cref="YsfFich"/> relies on to settle the parameters that public sources leave
/// ambiguous.
///
/// <para>Written from the code's definition rather than ported: the generator polynomials are
/// published constants and a Viterbi decoder is a textbook algorithm. Deliberately not taken from
/// MMDVMHost, which is GPLv2-only and would be incompatible with this project's GPLv3 (the same trap
/// documented for the D-STAR header decoders in CLAUDE.md).</para>
/// </summary>
public static class YsfConvolution
{
    private const int States = 16;      // K = 5 → four bits of history

    private const double Unreachable = double.MaxValue / 4;

    /// <summary>
    /// Decodes <paramref name="dibits"/> pairs of soft bits into <paramref name="output"/> bits.
    ///
    /// Soft values are the branch metrics directly: each input is the likelihood that the bit was a
    /// one, in [0,1], so a marginal symbol contributes proportionally less than a confident one
    /// instead of being rounded off before the decoder ever sees it.
    /// </summary>
    public static void Decode(ReadOnlySpan<double> softBits, Span<byte> output, uint g1, uint g2)
    {
        var steps = softBits.Length / 2;
        Span<double> cost = stackalloc double[States];
        Span<double> next = stackalloc double[States];

        // One byte of traceback per state per step: which predecessor won.
        var history = new byte[steps * States];

        cost.Fill(Unreachable);
        cost[0] = 0; // the encoder starts with a flushed register

        for (var step = 0; step < steps; step++)
        {
            next.Fill(Unreachable);
            var s0 = softBits[2 * step];
            var s1 = softBits[(2 * step) + 1];

            for (var state = 0; state < States; state++)
            {
                if (cost[state] >= Unreachable)
                {
                    continue;
                }

                for (var bit = 0; bit < 2; bit++)
                {
                    var register = (uint)((bit << 4) | state);
                    var expected0 = Parity(register & g1);
                    var expected1 = Parity(register & g2);

                    // Distance from what this branch would have transmitted to what arrived.
                    var metric = cost[state]
                        + Math.Abs(s0 - expected0)
                        + Math.Abs(s1 - expected1);

                    var successor = (int)(register >> 1);
                    if (metric < next[successor])
                    {
                        next[successor] = metric;
                        history[(step * States) + successor] = (byte)state;
                    }
                }
            }

            next.CopyTo(cost);
        }

        // Trace back from the best surviving state. The encoder flushes to zero, so state 0 is the
        // expected end — but the last four bits are that flush and are discarded by the caller, so
        // taking the best state rather than insisting on zero costs nothing and is kinder to a frame
        // whose tail was clipped.
        var best = 0;
        for (var state = 1; state < States; state++)
        {
            if (cost[state] < cost[best])
            {
                best = state;
            }
        }

        for (var step = steps - 1; step >= 0; step--)
        {
            var previous = history[(step * States) + best];

            // The input bit is the one that shifted into the top of the register.
            if (step < output.Length)
            {
                output[step] = (byte)(best >> 3);
            }

            best = previous;
        }
    }

    /// <summary>Encodes bits the way the transmitter does — for tests, and to state the code unambiguously.</summary>
    public static void Encode(ReadOnlySpan<byte> bits, Span<double> softBits, uint g1, uint g2)
    {
        var state = 0u;
        for (var i = 0; i < bits.Length; i++)
        {
            var register = ((uint)(bits[i] & 1) << 4) | state;
            softBits[2 * i] = Parity(register & g1);
            softBits[(2 * i) + 1] = Parity(register & g2);
            state = register >> 1;
        }
    }

    private static double Parity(uint value) => System.Numerics.BitOperations.PopCount(value) & 1;
}
