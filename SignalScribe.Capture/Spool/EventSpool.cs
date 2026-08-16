using System.Text.Json;
using SignalScribe.Contracts;

namespace SignalScribe.Capture.Spool;

/// <summary>
/// Append-only journal for ingest events the host couldn't be reached for. Capture never blocks
/// on the web host (CLAUDE.md invariant): failed posts land here and are replayed on reconnect.
/// Ingest is idempotent on (channel, StartUtc), so replay after a partial success is safe.
/// </summary>
public sealed class EventSpool(string spoolDirectory)
{
    private readonly string _path = Path.Combine(spoolDirectory, "events.jsonl");

    private readonly Lock _lock = new();

    public void Append(TransmissionIngest ingest)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(spoolDirectory);
            File.AppendAllText(_path, JsonSerializer.Serialize(ingest) + Environment.NewLine);
        }
    }

    /// <summary>Replays spooled events through <paramref name="post"/>; truncates the journal only if every post succeeds.</summary>
    public async Task<int> ReplayAsync(Func<TransmissionIngest, Task<bool>> post, CancellationToken ct)
    {
        string[] lines;
        lock (_lock)
        {
            if (!File.Exists(_path))
            {
                return 0;
            }

            lines = File.ReadAllLines(_path);
        }

        var replayed = 0;
        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var ingest = JsonSerializer.Deserialize<TransmissionIngest>(line);
            if (ingest is null || !await post(ingest))
            {
                return replayed; // host went away again — keep the journal, retry later
            }

            replayed++;
        }

        lock (_lock)
        {
            File.Delete(_path);
        }

        return replayed;
    }
}
