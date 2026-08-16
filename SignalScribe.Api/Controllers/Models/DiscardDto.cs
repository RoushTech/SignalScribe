namespace SignalScribe.Api.Controllers.Models;

public record DiscardDto(
    long Id,
    long FrequencyHz,
    DateTime StartUtc,
    int DurationMs,
    SignalScribe.Enums.DiscardReason Reason,
    double PeakDbfs,
    int VoicedMs,
    double SpeechBandRatio,
    double ModulationDepth,
    double SyllableRateHz,
    bool SustainedTone,
    double? CtcssHz,
    int? DcsCode);

public record ReasonCountDto(SignalScribe.Enums.DiscardReason Reason, int Count);

public record DiscardStatsDto(int Total, DateTime? OldestUtc, IReadOnlyList<ReasonCountDto> ByReason);
