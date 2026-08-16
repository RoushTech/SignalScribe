namespace SignalScribe.Api.Controllers.Models;

public record SearchHitDto(
    long SegmentId,
    long TransmissionId,
    long FrequencyHz,
    DateTime StartUtc,
    string Snippet);
