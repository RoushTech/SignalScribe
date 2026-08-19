using SignalScribe.Capture.Digital.Ysf;
using SignalScribe.Enums;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

/// <summary>
/// The Fusion FICH chain: deinterleave, Viterbi, Golay systematic bits, CRC — and the variant search
/// that settles the conventions public sources leave ambiguous.
///
/// <para>These tests prove the chain is <i>internally</i> consistent and that a wrong variant cannot
/// fake a decode. They deliberately cannot prove the chain matches Yaesu, because a synthetic
/// round-trip is self-consistent when wrong — the same trap CLAUDE.md records for D-STAR's sync and
/// interleave. Only real traffic settles that, which is what the variant search and the logged frame
/// counters exist for.</para>
/// </summary>
public class YsfFichTests(ITestOutputHelper output)
{
    private static readonly byte[] Fich = [0x20, 0x01, 0x08, 0x40];

    [Fact]
    public void AFichSurvivesItsOwnEncodeAndDecode()
    {
        var variant = YsfFichDecoder.Variants[0];
        var soft = new double[YsfFichDecoder.Dibits * 2];
        YsfFichDecoder.Encode(Fich, variant, soft);

        Assert.True(YsfFichDecoder.TryDecode(soft, variant, out var decoded));
        Assert.Equal(Fich, decoded!.Raw);
    }

    [Fact]
    public void EveryVariantRoundTripsUnderItsOwnConventions()
    {
        // Each variant has to be a working decoder in its own right — otherwise the search would be
        // choosing between candidates that were never all viable, and the one Fusion actually uses
        // might be a candidate that could never have decoded anything.
        foreach (var variant in YsfFichDecoder.Variants)
        {
            var soft = new double[YsfFichDecoder.Dibits * 2];
            YsfFichDecoder.Encode(Fich, variant, soft);
            Assert.True(YsfFichDecoder.TryDecode(soft, variant, out var decoded), $"variant {variant} failed to decode its own encoding");
            Assert.Equal(Fich, decoded!.Raw);
        }

        output.WriteLine($"  {YsfFichDecoder.Variants.Length} variants, all self-consistent");
    }

    /// <summary>
    /// The point of the whole design: the CRC, not the sync, decides. A frame encoded under one set
    /// of conventions must not decode under another, or the search would settle on whichever variant
    /// it tried first and report its findings with total confidence.
    /// </summary>
    [Fact]
    public void AFrameDoesNotDecodeUnderTheWrongVariant()
    {
        var truth = YsfFichDecoder.Variants[0];
        var soft = new double[YsfFichDecoder.Dibits * 2];
        YsfFichDecoder.Encode(Fich, truth, soft);

        var falsePositives = 0;
        foreach (var variant in YsfFichDecoder.Variants)
        {
            if (variant.Equals(truth))
            {
                continue;
            }

            // Correction is a no-op on an undamaged codeword, so the variant differing only in
            // whether it corrects is not a wrong answer — it is the same decoder reaching the same
            // frame by a longer route, and it must agree.
            if (variant.Equals(truth with { CorrectGolay = !truth.CorrectGolay }))
            {
                Assert.True(YsfFichDecoder.TryDecode(soft, variant, out var twin));
                Assert.Equal(Fich, twin!.Raw);
                continue;
            }

            if (YsfFichDecoder.TryDecode(soft, variant, out _))
            {
                falsePositives++;
                output.WriteLine($"  {variant} also decoded it");
            }
        }

        // Every wrong variant must reject it. This started at 8 and the eight were real: the encoder
        // left the half of each Golay block it does not use as zeros, and a variant reading that
        // half found an all-zero payload, which a zero-seeded CRC over zero data accepts. That is a
        // live failure mode too — a fade can drive the soft bits to a constant — so the decoder now
        // refuses an all-zero frame outright and the encoder fills the unused half.
        output.WriteLine($"  {falsePositives}/{YsfFichDecoder.Variants.Length - 1} wrong variants accepted the frame");
        Assert.Equal(0, falsePositives);
    }

    [Fact]
    public void NoiseDoesNotProduceAFich()
    {
        var rng = new Random(17);
        var accepted = 0;
        for (var trial = 0; trial < 200; trial++)
        {
            var soft = new double[YsfFichDecoder.Dibits * 2];
            for (var i = 0; i < soft.Length; i++)
            {
                soft[i] = rng.NextDouble();
            }

            foreach (var variant in YsfFichDecoder.Variants)
            {
                if (YsfFichDecoder.TryDecode(soft, variant, out _))
                {
                    accepted++;
                }
            }
        }

        // 200 frames × 64 variants ≈ 12,800 chances at a 16-bit CRC: a couple of accidents is
        // expected and is exactly why the framer demands repeats from one variant.
        output.WriteLine($"  {accepted} accidental CRC passes in 200 noise frames across all variants");
        Assert.True(accepted < 10, $"{accepted} accidental passes — far more than a 16-bit CRC should allow");
    }

    [Fact]
    public void TheFramerFindsFramesInASyntheticTransmission()
    {
        var variant = YsfFichDecoder.Variants[0];
        var framer = new YsfFramer();
        foreach (var symbol in Transmission(variant, frames: 6, invert: false))
        {
            framer.Feed(symbol);
        }

        Assert.True(framer.Decoded, $"{framer.SyncCount} syncs, {framer.FichCount} FICHs, no variant settled");
        Assert.Equal(variant, framer.SettledVariant);
        Assert.Equal(DetectedMode.Ysf, framer.ToHeader()!.Mode);
        output.WriteLine($"  {framer.SyncCount} syncs, {framer.FichCount} FICHs, settled on {framer.SettledVariant}");
    }

    [Fact]
    public void TheFramerFindsFramesWhenTheDiscriminatorIsInverted()
    {
        var variant = YsfFichDecoder.Variants[0];
        var framer = new YsfFramer();
        foreach (var symbol in Transmission(variant, frames: 6, invert: true))
        {
            framer.Feed(symbol);
        }

        Assert.True(framer.Decoded, $"{framer.SyncCount} syncs, {framer.FichCount} FICHs, no variant settled");
    }

    [Fact]
    public void OneFrameIsNotEnoughToClaimFusion()
    {
        // A 16-bit CRC tried 64 ways passes on noise often enough to see; repetition is what makes
        // it proof, the same argument the D-STAR cadence rule rests on.
        var framer = new YsfFramer();
        foreach (var symbol in Transmission(YsfFichDecoder.Variants[0], frames: 1, invert: false))
        {
            framer.Feed(symbol);
        }

        Assert.False(framer.Decoded);
        Assert.Null(framer.ToHeader());
    }

    [Fact]
    public void AnalogNoiseNeverNamesFusion()
    {
        var rng = new Random(23);
        var framer = new YsfFramer();
        for (var i = 0; i < 200_000; i++)
        {
            framer.Feed((rng.NextDouble() * 3) - 1.5);
        }

        Assert.False(framer.Decoded, $"noise settled on {framer.SettledVariant} after {framer.FichCount} FICHs");
        output.WriteLine($"  {framer.SyncCount} syncs, {framer.FichCount} stray CRC passes, decoded={framer.Decoded}");
    }

    /// <summary>
    /// The header must say plainly that callsigns are not available yet. Fusion carries them in the
    /// data channel, which is a second decode chain — and an empty space where a callsign belongs
    /// reads as "nobody was identified" rather than "this is not built yet".
    /// </summary>
    [Fact]
    public void TheHeaderIsHonestAboutCallsigns()
    {
        var framer = new YsfFramer();
        foreach (var symbol in Transmission(YsfFichDecoder.Variants[0], frames: 6, invert: false))
        {
            framer.Feed(symbol);
        }

        var header = framer.ToHeader()!;
        Assert.Null(header.Callsign);
        Assert.Contains(header.Fields, f => f.Name == "Callsign" && f.Value.Contains("not decoded"));
        Assert.Contains(header.Fields, f => f.Name == "FICH");
    }

    /// <summary>
    /// The interpreted FICH fields stay hidden until real traffic has confirmed the layout — the
    /// CRC cannot vouch for what the bits *mean*, only that they arrived intact.
    /// </summary>
    [Fact]
    public void InterpretedFieldsAreWithheldUntilTheLayoutIsConfirmed()
    {
        var fich = new YsfFich(Fich, YsfFichDecoder.Variants[0]);
        var fields = YsfFichDecoder.Describe(fich);

        Assert.Equal("FICH", Assert.Single(fields).Name);
        Assert.False(YsfFich.LayoutConfirmed, "once confirmed on air, this test should assert the fields instead");
    }

    /// <summary>Frame sync, FICH, then payload — 480 symbols per frame, as a radio sends it.</summary>
    private static double[] Transmission(YsfFichVariant variant, int frames, bool invert)
    {
        var rng = new Random(5);
        var symbols = new List<double>(frames * 480);

        // Lead-in, so nothing depends on the stream starting exactly at a sync.
        for (var i = 0; i < 64; i++)
        {
            symbols.Add(Level(rng.Next(4)));
        }

        for (var frame = 0; frame < frames; frame++)
        {
            for (var i = 19; i >= 0; i--)
            {
                var dibit = (int)((YsfFramer.FrameSync >> (i * 2)) & 3);
                symbols.Add(Level(dibit));
            }

            var soft = new double[YsfFichDecoder.Dibits * 2];
            YsfFichDecoder.Encode([0x20, 0x01, (byte)(0x08 | frame), 0x40], variant, soft);
            for (var i = 0; i < YsfFichDecoder.Dibits; i++)
            {
                symbols.Add(Level((int)((soft[2 * i] > 0.5 ? 2 : 0) + (soft[(2 * i) + 1] > 0.5 ? 1 : 0))));
            }

            for (var i = 0; i < 360; i++)
            {
                symbols.Add(Level(rng.Next(4))); // vocoder payload, opaque to us
            }
        }

        var result = symbols.ToArray();
        if (invert)
        {
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = -result[i];
            }
        }

        return result;
    }

    /// <summary>The shared C4FM mapping: 01 → +3, 00 → +1, 10 → −1, 11 → −3, scaled so outer sits near ±1.5.</summary>
    private static double Level(int dibit) => dibit switch
    {
        0b01 => 1.5,
        0b00 => 0.5,
        0b10 => -0.5,
        _ => -1.5,
    };
}
