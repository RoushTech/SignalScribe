using SignalScribe.Contracts;

namespace SignalScribe.Api.Services;

/// <summary>Latest waterfall row, in memory only — for browser initial paint and smoke tests. Live rows stream over the hub.</summary>
public class SpectrumCache
{
    private volatile SpectrumRow? _latest;

    public SpectrumRow? Latest
    {
        get => _latest;
        set => _latest = value;
    }
}
