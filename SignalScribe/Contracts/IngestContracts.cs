using SignalScribe.Enums;

namespace SignalScribe.Contracts;

// Wire contracts between the capture daemon / workers and the web host's api/internal/ surface.
// These are shared here (not in Api/Controllers/Models) because the daemons serialize them too.

public record MarkerIngest(MarkerType Type, int OffsetMs, double Confidence);

/// <summary>
/// One AX.25 frame decoded from a transmission, rendered as TNC2 — the canonical monitor format
/// (<c>SRC&gt;DEST,PATH:info</c>) every packet tool speaks.
/// </summary>
public record PacketIngest(int OffsetMs, int DurationMs, string Tnc2, string Source, string Destination);

/// <summary>One named value read out of a digital mode's header, in the order the operator should see it.</summary>
public record HeaderField(string Name, string Value);

/// <summary>
/// Header metadata recovered from a digital transmission, whatever the mode.
///
/// Every digital voice mode sends its routing and identity in a channel *beside* the vocoder —
/// D-STAR's 41-byte header, Fusion's FICH and data channel, DMR's link control, P25's headers — all
/// of them FEC-protected and none of them needing AMBE to read. So this is deliberately shaped
/// around the fields rather than around any one protocol: a framer reports whatever its mode
/// actually carries, and everything downstream stores and shows all of it without knowing the mode.
/// Adding a mode therefore costs a framer and nothing else.
///
/// <see cref="Summary"/> is the one-line reading for lists and search; <see cref="Fields"/> is the
/// full record. <see cref="Callsign"/> is the transmitting station, when the mode names one, so a
/// digital over joins up with the same operator heard on analog voice.
/// </summary>
public record DigitalHeaderIngest(
    int OffsetMs,
    DetectedMode Mode,
    string? Callsign,
    string Summary,
    List<HeaderField> Fields);

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
    int? DcsCode = null,
    DetectedMode Mode = DetectedMode.Unknown,
    List<PacketIngest>? Packets = null,
    List<DigitalHeaderIngest>? DigitalHeaders = null);

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
    int? DcsCode = null,
    DetectedMode Mode = DetectedMode.Unknown);

/// <summary>What a known channel carries, so capture can decide where to run the expensive decoders.</summary>
public record ChannelModeInfo(long FrequencyHz, DetectedMode Mode);

/// <summary>
/// A known channel's squelch reference and whether capture may keep learning it. Sent to capture at
/// startup so floors survive a restart instead of being relearned from silence, and refreshed with
/// the rest of the channel state so an operator pinning a floor takes effect without a restart.
/// </summary>
public record ChannelSquelchInfo(long FrequencyHz, double? NoiseFloorDbfs, bool Adaptive);

/// <summary>A floor capture has learned, reported back so it outlives the process that learned it.</summary>
public record NoiseFloorReport(long FrequencyHz, double NoiseFloorDbfs);

public record TransmissionIngestResult(long TransmissionId, bool AlreadyExisted);

public record JobClaimRequest(string WorkerId, JobType[] Types, int MaxJobs);

public record ClaimedJob(long Id, JobType Type, string PayloadJson);

public record JobCompleteRequest(string WorkerId, bool Success, string? Error);
