namespace SignalScribe.Api.Controllers.Models;

public record DiscardDto(
    long Id,
    long FrequencyHz,
    DateTime StartUtc,
    int DurationMs,
    string Reason,
    double PeakDbfs,
    int VoicedMs,
    double SpeechBandRatio,
    double ModulationDepth,
    double SyllableRateHz,
    bool SustainedTone);

public record ReasonCountDto(string Reason, int Count);

public record DiscardStatsDto(int Total, DateTime? OldestUtc, IReadOnlyList<ReasonCountDto> ByReason);
