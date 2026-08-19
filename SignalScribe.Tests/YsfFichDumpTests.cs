using SignalScribe.Capture.Digital.Ysf;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

/// <summary>
/// The offline-fixture path. A Fusion transmission that syncs but never decodes is the only thing
/// standing between this decoder and working, and it is also the one signal that cannot be
/// recovered after the fact — so capturing it correctly is load-bearing, and is tested rather than
/// assumed before being left to collect traffic unattended.
/// </summary>
public class YsfFichDumpTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"ss-fich-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>
    /// The case the dump exists for: frame syncs found, nothing CRC-valid. The blocks must be kept,
    /// because this is exactly the signal that has to be replayed against new conventions.
    /// </summary>
    [Fact]
    public void AFramerThatSyncsButCannotDecodeStillKeepsTheBlocks()
    {
        var framer = new YsfFramer();
        foreach (var symbol in SyncedButUndecodable(frames: 5))
        {
            framer.Feed(symbol);
        }

        output.WriteLine($"  {framer.SyncCount} syncs, {framer.FichCount} FICHs, {framer.CapturedBlocks.Count} blocks kept");
        Assert.True(framer.SyncCount > 0, "the fixture must actually sync, or it is not the failure being reproduced");
        Assert.False(framer.Decoded);
        Assert.NotEmpty(framer.CapturedBlocks);
        Assert.All(framer.CapturedBlocks, b => Assert.Equal(YsfFichDecoder.Dibits * 2, b.Length));
    }

    [Fact]
    public void BlocksSurviveAWriteAndReadRoundTrip()
    {
        Directory.CreateDirectory(_directory);
        var clip = Path.Combine(_directory, "145312500_210409_013.ogg");

        var framer = new YsfFramer();
        foreach (var symbol in SyncedButUndecodable(frames: 4))
        {
            framer.Feed(symbol);
        }

        YsfFichDump.Write(clip, framer.SyncCount, framer.CapturedBlocks);

        var path = Path.ChangeExtension(clip, YsfFichDump.Extension);
        Assert.True(File.Exists(path), "no dump was written for a synced-but-undecoded transmission");

        var read = YsfFichDump.Read(path);
        Assert.Equal(framer.CapturedBlocks.Count, read.Count);
        for (var i = 0; i < read.Count; i++)
        {
            for (var j = 0; j < read[i].Length; j++)
            {
                // Three decimals is the stored precision, and is far finer than the slicer's own
                // confidence — the soft bits only have to preserve which side of the decision they
                // fell on and roughly how firmly.
                Assert.Equal(framer.CapturedBlocks[i][j], read[i][j], 3);
            }
        }
    }

    [Fact]
    public void ADecodedTransmissionNeedsNoDump()
    {
        // Nothing to study when it already works, and writing files for every Fusion over would be
        // a slow leak on the audio volume.
        var variant = YsfFichDecoder.Variants[0];
        var framer = new YsfFramer();
        foreach (var symbol in Decodable(variant, frames: 6))
        {
            framer.Feed(symbol);
        }

        Assert.True(framer.Decoded);
    }

    [Fact]
    public void WritingToAnUnwritablePathDoesNotThrow()
    {
        // A diagnostic must never cost a recording, so failure here is swallowed by design.
        YsfFichDump.Write("/nonexistent-directory/clip.ogg", 3, [new double[YsfFichDecoder.Dibits * 2]]);
    }

    /// <summary>Frames whose sync is real but whose FICH is noise — the on-air failure, reproduced.</summary>
    private static double[] SyncedButUndecodable(int frames)
    {
        var rng = new Random(13);
        var symbols = new List<double>(frames * 480);
        for (var frame = 0; frame < frames; frame++)
        {
            for (var i = 19; i >= 0; i--)
            {
                symbols.Add(Level((int)((YsfFramer.FrameSync >> (i * 2)) & 3)));
            }

            for (var i = 0; i < YsfFichDecoder.Dibits + 360; i++)
            {
                symbols.Add(Level(rng.Next(4)));
            }
        }

        return [.. symbols];
    }

    private static double[] Decodable(YsfFichVariant variant, int frames)
    {
        var rng = new Random(5);
        var symbols = new List<double>(frames * 480);
        for (var frame = 0; frame < frames; frame++)
        {
            for (var i = 19; i >= 0; i--)
            {
                symbols.Add(Level((int)((YsfFramer.FrameSync >> (i * 2)) & 3)));
            }

            var soft = new double[YsfFichDecoder.Dibits * 2];
            YsfFichDecoder.Encode([0x20, 0x01, 0x08, 0x40], variant, soft);
            for (var i = 0; i < YsfFichDecoder.Dibits; i++)
            {
                symbols.Add(Level((int)((soft[2 * i] > 0.5 ? 2 : 0) + (soft[(2 * i) + 1] > 0.5 ? 1 : 0))));
            }

            for (var i = 0; i < 360; i++)
            {
                symbols.Add(Level(rng.Next(4)));
            }
        }

        return [.. symbols];
    }

    private static double Level(int dibit) => dibit switch
    {
        0b01 => 1.5,
        0b00 => 0.5,
        0b10 => -0.5,
        _ => -1.5,
    };
}
