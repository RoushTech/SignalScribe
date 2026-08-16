using SignalScribe.Enums;

namespace SignalScribe.Api.Controllers.Models;

public record ChannelDto(
    int Id,
    long FrequencyHz,
    string Label,
    ChannelType Type,
    bool Enabled,
    string? Callsign,
    string? Description,
    double? CtcssToneHz,
    string? Notes,
    double? NoiseFloorDbfs,
    double? MeasuredCtcssToneHz,
    int TransmissionCount,
    DateTime? LastHeardUtc,
    string? AutoDisabledReason,
    DateTime? LastSpeechUtc);

public record ChannelUpsertRequest(
    long FrequencyHz,
    string Label,
    ChannelType Type,
    bool Enabled,
    string? Callsign,
    string? Description,
    double? CtcssToneHz,
    string? Notes);
