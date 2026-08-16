using SignalScribe.Enums;

namespace SignalScribe.Api.Controllers.Models;

public record ProcessingStatsDto(
    int Pending,
    int Leased,
    int Completed,
    int Failed,
    DateTime? OldestPendingUtc,
    IReadOnlyList<TypeCountDto> PendingByType);

public record TypeCountDto(JobType Type, int Count);

public record FailedJobDto(
    long Id,
    JobType Type,
    int Attempts,
    string? Error,
    DateTime CreatedUtc,
    DateTime? CompletedUtc);
