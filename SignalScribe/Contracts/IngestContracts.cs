using SignalScribe.Enums;

namespace SignalScribe.Contracts;

// Wire contracts between the capture daemon / workers and the web host's api/internal/ surface.
// These are shared here (not in Api/Controllers/Models) because the daemons serialize them too.

public record MarkerIngest(MarkerType Type, int OffsetMs, double Confidence);

public record TransmissionIngest(
    long FrequencyHz,
    DateTime StartUtc,
    DateTime EndUtc,
    string AudioPath,
    double PeakDbfs,
    double? MeanCarrierOffsetHz,
    bool IsDouble,
    List<MarkerIngest> Markers,
    int VoicedMs = 0,
    double? CtcssHz = null,
    int? DcsCode = null);

/// <summary>A clip the capture-side gate rejected. Kept briefly so the operator can hear it and see why.</summary>
public record DiscardIngest(
    long FrequencyHz,
    DateTime StartUtc,
    DateTime EndUtc,
    string AudioPath,
    double PeakDbfs,
    SignalScribe.Enums.DiscardReason Reason,
    int VoicedMs,
    double SpeechBandRatio,
    double ModulationDepth,
    double SyllableRateHz,
    bool SustainedTone,
    double? CtcssHz = null,
    int? DcsCode = null);

public record TransmissionIngestResult(long TransmissionId, bool AlreadyExisted);

public record JobClaimRequest(string WorkerId, JobType[] Types, int MaxJobs);

public record ClaimedJob(long Id, JobType Type, string PayloadJson);

public record JobCompleteRequest(string WorkerId, bool Success, string? Error);
