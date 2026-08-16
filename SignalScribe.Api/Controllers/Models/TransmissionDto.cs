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
    IReadOnlyList<SegmentDto> Segments);

public record SegmentDto(
    long Id,
    int StartMs,
    int EndMs,
    string? Transcript,
    string? Callsign,
    long? SpeakerId);
