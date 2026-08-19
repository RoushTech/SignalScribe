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
    int? DcsCode,
    string? Notes,
    double? NoiseFloorDbfs,
    bool AdaptiveSquelch,
    double? MeasuredCtcssToneHz,
    int? MeasuredDcsCode,
    DetectedMode? Modulation,
    DetectedMode? MeasuredMode,
    DateTime? ModeUpdatedUtc,
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
    string? Notes,
    DetectedMode? Modulation = null,
    /// <summary>
    /// DCS code, as the octal number operators quote. Mutually exclusive with
    /// <see cref="CtcssToneHz"/> — they are alternative systems, never both.
    /// </summary>
    int? DcsCode = null,
    /// <summary>False pins the channel's squelch reference at its stored value.</summary>
    bool AdaptiveSquelch = true,
    /// <summary>Squelch reference to pin, when adaptive tracking is off.</summary>
    double? NoiseFloorDbfs = null);
