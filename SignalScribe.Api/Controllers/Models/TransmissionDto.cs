using SignalScribe.Enums;

namespace SignalScribe.Api.Controllers.Models;

public record TransmissionDto(
    long Id,
    int ChannelId,
    long FrequencyHz,
    string ChannelLabel,
    DateTime StartUtc,
    DateTime? EndUtc,
    bool IsDouble,
    string AudioPath,
    string Status,
    double? CtcssHz,
    int? DcsCode,
    double? ChannelCtcssHz,
    int? ChannelDcsCode,
    DetectedMode Mode,
    DetectedMode? ChannelMode,
    IReadOnlyList<SegmentDto> Segments);

public record SegmentDto(
    long Id,
    int StartMs,
    int EndMs,
    string? Transcript,
    string? Callsign,
    long? SpeakerId,
    /// <summary>
    /// For a decoded data frame, a human reading of it. Derived on the way out rather than stored:
    /// the raw frame in <see cref="Transcript"/> is the record, so improving this costs no migration
    /// and applies to traffic already captured. Null for speech.
    /// </summary>
    string? Summary = null,
    /// <summary>
    /// Every field a digital mode's header carried, in display order; null for speech. On a mode
    /// whose voice needs a vocoder we do not have, this is the whole of what was said — so it is
    /// surfaced in full rather than reduced to <see cref="Transcript"/>'s one-line reading.
    /// </summary>
    IReadOnlyList<Contracts.HeaderField>? HeaderFields = null);
