namespace SignalScribe.Api.Controllers.Models;

public record SessionDto(
    long Id,
    int ChannelId,
    string ChannelLabel,
    DateTime StartUtc,
    DateTime? EndUtc,
    bool IsNet,
    long? NetId,
    string? NetName,
    int TransmissionCount,
    string? Summary,
    string? SummaryModel);
