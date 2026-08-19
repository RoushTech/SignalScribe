namespace SignalScribe.Modem;

/// <summary>
/// Collapses duplicate frames decoded near-simultaneously by parallel
/// demodulator profiles.  Keyed by an FNV-1a hash of the frame bytes within a
/// short window — long enough to cover profile skew, short enough that a
/// station legitimately repeating the same packet is not suppressed.
/// </summary>
public sealed class FrameDeduper(TimeSpan? window = null)
{
    private readonly TimeSpan _window = window ?? TimeSpan.FromSeconds(2);
    private readonly Dictionary<ulong, DateTime> _seen = [];
    private readonly Lock _lock = new();

    /// <summary>
    /// Returns <see langword="true"/> if the frame has not been seen within
    /// the dedup window (and records it), <see langword="false"/> for a duplicate.
    /// </summary>
    public bool IsNewFrame(ReadOnlySpan<byte> frame, DateTime nowUtc)
    {
        var hash = Fnv1a64(frame);

        lock (_lock)
        {
            // Opportunistic prune so the dictionary cannot grow unboundedly.
            if (_seen.Count > 256)
            {
                var cutoff = nowUtc - _window;
                foreach (var key in _seen.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
                    _seen.Remove(key);
            }

            if (_seen.TryGetValue(hash, out var lastSeen) && nowUtc - lastSeen < _window)
            {
                _seen[hash] = lastSeen; // keep the original timestamp
                return false;
            }

            _seen[hash] = nowUtc;
            return true;
        }
    }

    private static ulong Fnv1a64(ReadOnlySpan<byte> data)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in data)
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }
        return hash;
    }
}
