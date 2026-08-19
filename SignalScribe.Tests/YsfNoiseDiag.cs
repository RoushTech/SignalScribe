using SignalScribe.Capture.Digital.Ysf;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

/// <summary>
/// How the Fusion decoder degrades with symbol errors — the measurement that separates "our
/// conventions are wrong" from "the signal was never good enough".
///
/// On air, 145.310 produced frame syncs on only three or four of the ten-odd frames in each
/// transmission and no CRC-valid FICH at all. Those two numbers are linked: the sync is 40 bits
/// with four tolerated errors, so the rate at which it lands pins down the symbol error rate, and
/// the symbol error rate says whether a FICH could have survived even with every convention right.
///
/// This sweeps known-correct parameters across error rates and reports both. If the observed sync
/// rate corresponds to an error rate where the FICH cannot decode regardless, then searching more
/// conventions is the wrong next move and symbol recovery is the real problem.
/// </summary>
public class YsfNoiseDiag(ITestOutputHelper output)
{
    [Fact]
    public void SweepSymbolErrorRate()
    {
        var variant = YsfFichDecoder.Variants.First(v => v.CorrectGolay);
        var raw = variant with { CorrectGolay = false };

        output.WriteLine("  symbol   sync    FICH     FICH");
        output.WriteLine("   error   rate   golay      raw");

        foreach (var errorRate in new[] { 0.0, 0.01, 0.02, 0.03, 0.05, 0.08, 0.10, 0.15, 0.20 })
        {
            const int Frames = 400;
            var rng = new Random(97);
            int syncs = 0, golayDecodes = 0, rawDecodes = 0;

            for (var frame = 0; frame < Frames; frame++)
            {
                // The sync word as it would arrive, with symbol errors sprinkled in.
                var syncErrors = 0;
                for (var i = 0; i < 20; i++)
                {
                    if (rng.NextDouble() < errorRate)
                    {
                        // A symbol error corrupts one or both bits of its dibit.
                        syncErrors += rng.NextDouble() < 0.5 ? 1 : 2;
                    }
                }

                if (syncErrors <= 4)
                {
                    syncs++;
                }

                var soft = new double[YsfFichDecoder.Dibits * 2];
                YsfFichDecoder.Encode([0x20, 0x01, (byte)(0x08 | (frame & 7)), 0x40], variant, soft);
                for (var i = 0; i < YsfFichDecoder.Dibits; i++)
                {
                    if (rng.NextDouble() >= errorRate)
                    {
                        continue;
                    }

                    soft[2 * i] = 1 - soft[2 * i];
                    if (rng.NextDouble() < 0.5)
                    {
                        soft[(2 * i) + 1] = 1 - soft[(2 * i) + 1];
                    }
                }

                if (YsfFichDecoder.TryDecode(soft, variant, out _))
                {
                    golayDecodes++;
                }

                if (YsfFichDecoder.TryDecode(soft, raw, out _))
                {
                    rawDecodes++;
                }
            }

            output.WriteLine(
                $"  {errorRate,6:P0} {syncs * 100.0 / Frames,6:F0}% {golayDecodes * 100.0 / Frames,7:F0}% {rawDecodes * 100.0 / Frames,7:F0}%");
        }

        output.WriteLine(string.Empty);
        output.WriteLine("  On air: ~30% sync rate, 0% FICH. Read across to the error rate that");
        output.WriteLine("  produces a ~30% sync rate and see whether any FICH was ever plausible.");
    }
}
