using SignalScribe.Capture.Digital.Ysf;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

/// <summary>
/// The extended Golay(24,12) code under Fusion's FICH. It must correct any three errors and, just as
/// importantly, must refuse the fourth rather than decode confidently to the wrong codeword.
/// </summary>
public class Golay24Tests(ITestOutputHelper output)
{
    /// <summary>
    /// The decoder carries a syndrome back through B to find data-half errors, which is only valid
    /// because B is its own inverse. That property is an unstated assumption of the algorithm and
    /// silently breaks three-error correction if the matrix is ever mistyped, so it is asserted
    /// rather than trusted.
    /// </summary>
    [Fact]
    public void TheGeneratorMatrixIsItsOwnInverse()
    {
        for (ushort data = 0; data < 4096; data++)
        {
            var there = Golay24.Encode(data) & 0xFFF;
            var back = Golay24.Encode((ushort)there) & 0xFFF;
            Assert.Equal(data, (ushort)back);
        }
    }

    [Fact]
    public void EveryCodewordSurvivesAClearChannel()
    {
        for (ushort data = 0; data < 4096; data++)
        {
            Assert.Equal(data, Golay24.Decode(Golay24.Encode(data)));
        }
    }

    [Fact]
    public void AnySingleErrorIsCorrected()
    {
        for (ushort data = 0; data < 4096; data += 7)
        {
            var codeword = Golay24.Encode(data);
            for (var bit = 0; bit < 24; bit++)
            {
                Assert.Equal(data, Golay24.Decode(codeword ^ (1u << bit)));
            }
        }
    }

    [Fact]
    public void AnyTwoErrorsAreCorrected()
    {
        for (ushort data = 0; data < 4096; data += 101)
        {
            var codeword = Golay24.Encode(data);
            for (var a = 0; a < 24; a++)
            {
                for (var b = a + 1; b < 24; b++)
                {
                    Assert.Equal(data, Golay24.Decode(codeword ^ (1u << a) ^ (1u << b)));
                }
            }
        }
    }

    [Fact]
    public void AnyThreeErrorsAreCorrected()
    {
        // Three is the code's guarantee and the reason it is worth running at all on a signal whose
        // frame syncs were only landing three times in ten.
        var rng = new Random(19);
        for (var trial = 0; trial < 4_000; trial++)
        {
            var data = (ushort)rng.Next(4096);
            var codeword = Golay24.Encode(data);
            var errors = 0u;
            while (System.Numerics.BitOperations.PopCount(errors) < 3)
            {
                errors |= 1u << rng.Next(24);
            }

            Assert.Equal(data, Golay24.Decode(codeword ^ errors));
        }
    }

    [Fact]
    public void FourErrorsAreRefusedFarMoreOftenThanTheyAreMisdecoded()
    {
        // Beyond the guarantee the code cannot be right, so what matters is that it says so instead
        // of handing a wrong answer to the CRC above wearing a straight face.
        var rng = new Random(29);
        int refused = 0, wrong = 0;
        for (var trial = 0; trial < 4_000; trial++)
        {
            var data = (ushort)rng.Next(4096);
            var codeword = Golay24.Encode(data);
            var errors = 0u;
            while (System.Numerics.BitOperations.PopCount(errors) < 4)
            {
                errors |= 1u << rng.Next(24);
            }

            var decoded = Golay24.Decode(codeword ^ errors);
            if (decoded is null)
            {
                refused++;
            }
            else if (decoded != data)
            {
                wrong++;
            }
        }

        output.WriteLine($"  4-error words: {refused} refused, {wrong} misdecoded of 4000");
        Assert.True(refused > wrong, $"only {refused} refused against {wrong} misdecoded");
    }

    [Fact]
    public void CorrectionRecoversAFichThroughErrorsThatWouldOtherwiseKillIt()
    {
        // The end-to-end point: the same frame, damaged, decodes with correction and does not
        // without it. This is what a marginal repeater signal looks like.
        var correcting = YsfFichDecoder.Variants.First(v => v.CorrectGolay);
        var raw = correcting with { CorrectGolay = false };

        var soft = new double[YsfFichDecoder.Dibits * 2];
        YsfFichDecoder.Encode([0x20, 0x01, 0x08, 0x40], correcting, soft);

        // Flip a couple of dibits, as a fading signal does.
        soft[10] = 1 - soft[10];
        soft[11] = 1 - soft[11];
        soft[64] = 1 - soft[64];

        var withCorrection = YsfFichDecoder.TryDecode(soft, correcting, out var corrected);
        var withoutCorrection = YsfFichDecoder.TryDecode(soft, raw, out _);

        output.WriteLine($"  with Golay correction: {withCorrection}; without: {withoutCorrection}");
        Assert.True(withCorrection, "correction should have recovered the frame");
        Assert.Equal(new byte[] { 0x20, 0x01, 0x08, 0x40 }, corrected!.Raw);
    }
}
