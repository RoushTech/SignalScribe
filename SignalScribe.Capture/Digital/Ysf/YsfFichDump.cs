using System.Globalization;

namespace SignalScribe.Capture.Digital.Ysf;

/// <summary>
/// Writes and reads the raw FICH soft bits of a Fusion transmission that synced but never decoded.
///
/// This is the fixture format that makes the decoder workable offline. The recovered symbol stream
/// exists nowhere else — clips are demodulated, de-emphasised, limited Opus audio, from which
/// symbols cannot be recovered — so without this a transmission that defeats the decoder is gone,
/// and every candidate set of conventions costs a rebuild and a wait for the band. With it, one
/// evening of traffic becomes a permanent corpus that any number of decoders can be tried against.
///
/// Plain text on purpose: it is read far more often by a person staring at a failure than by code.
/// </summary>
public static class YsfFichDump
{
    public const string Extension = ".fich.txt";

    /// <summary>Writes the blocks beside the clip. Never throws — a diagnostic must not cost a recording.</summary>
    public static void Write(string clipPath, int syncCount, IReadOnlyList<double[]> blocks)
    {
        try
        {
            using var writer = new StreamWriter(Path.ChangeExtension(clipPath, Extension));
            writer.WriteLine($"# Fusion FICH soft bits — {syncCount} syncs, {blocks.Count} blocks, none CRC-valid");
            writer.WriteLine($"# one line per block, {YsfFichDecoder.Dibits} dibits as {YsfFichDecoder.Dibits * 2} soft bits in [0,1]");
            foreach (var block in blocks)
            {
                writer.WriteLine(string.Join(' ', block.Select(b => b.ToString("F3", CultureInfo.InvariantCulture))));
            }
        }
        catch (Exception)
        {
            // Diagnostics must never cost a recording.
        }
    }

    /// <summary>Reads blocks back for offline decoding experiments; malformed lines are skipped.</summary>
    public static List<double[]> Read(string path)
    {
        var blocks = new List<double[]>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith('#') || line.Length == 0)
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != YsfFichDecoder.Dibits * 2)
            {
                continue;
            }

            var block = new double[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                block[i] = double.Parse(parts[i], CultureInfo.InvariantCulture);
            }

            blocks.Add(block);
        }

        return blocks;
    }
}
