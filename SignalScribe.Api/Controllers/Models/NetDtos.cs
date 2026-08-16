using SignalScribe.Enums;

namespace SignalScribe.Api.Controllers.Models;

public record NetDto(
    long Id,
    int ChannelId,
    string? Name,
    string? Description,
    NetScheduleSource Source,
    DayOfWeek? DayOfWeekUtc,
    TimeOnly? StartTimeUtc,
    int? DurationMinutes,
    int SessionCount,
    DateTime? LastSessionUtc);

public record NetUpsertRequest(
    int ChannelId,
    string? Name,
    string? Description,
    DayOfWeek? DayOfWeekUtc,
    TimeOnly? StartTimeUtc,
    int? DurationMinutes);
