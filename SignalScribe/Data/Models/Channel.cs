using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SignalScribe.Enums;

namespace SignalScribe.Data.Models;

public class Channel : IEntityTypeConfiguration<Channel>
{
    public int Id { get; set; }

    public long FrequencyHz { get; set; }

    public string Label { get; set; } = string.Empty;

    public ChannelType Type { get; set; } = ChannelType.Unknown;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Why the channel was auto-disabled, or null if it wasn't. A disabled channel is not in the
    /// capture daemon's known set, so activity on it must pass the voice gate like any unknown
    /// frequency — that is what stops a data frequency (APRS, packet, a stuck carrier) from
    /// recording every burst forever. The operator can re-enable it at any time.
    /// </summary>
    public string? AutoDisabledReason { get; set; }

    /// <summary>Last time a transmission here produced an actual transcript. Null means never.</summary>
    public DateTime? LastSpeechUtc { get; set; }

    /// <summary>Repeater (or club) callsign — user-entered.</summary>
    public string? Callsign { get; set; }

    public string? Description { get; set; }

    /// <summary>User-configured CTCSS/PL tone in Hz. The *measured* tone lives in <see cref="LearnedState"/> — keep config and learned state separate.</summary>
    public double? CtcssToneHz { get; set; }

    /// <summary>
    /// User-configured DCS code, as the octal number operators quote. The measured code lives in
    /// <see cref="LearnedState"/>, same config-versus-learned split as the tone.
    ///
    /// CTCSS and DCS are alternative systems and a channel never carries both — a tone beating
    /// against the 134.4 bps DCS bit clock produces a repeating, Golay-valid phantom code, which is
    /// how 146.640 (CTCSS 146.2) once reported DCS 073. Setting either through
    /// <see cref="SetSquelchTone"/> clears the other so the pair can never disagree.
    /// </summary>
    public int? DcsCode { get; set; }

    /// <summary>
    /// Sets one squelch system and clears the other, because they are alternatives rather than a
    /// pair. Passing null for both means carrier squelch.
    /// </summary>
    public void SetSquelchTone(double? ctcssToneHz, int? dcsCode)
    {
        // A caller that supplies both is confused about the hardware, not expressing a preference:
        // prefer the tone, since that is what the overwhelming majority of repeaters use.
        CtcssToneHz = ctcssToneHz;
        DcsCode = ctcssToneHz is null ? dcsCode : null;
    }

    /// <summary>
    /// Operator-declared modulation. Null means "whatever capture measures", which is the usual case;
    /// setting it pins a channel the operator knows better than the classifier does. The *measured*
    /// mode lives in <see cref="LearnedState"/>, same config-versus-learned split as the tone above.
    /// </summary>
    public DetectedMode? Modulation { get; set; }

    public string? Notes { get; set; }

    /// <summary>
    /// Squelch reference, dBFS — the level this channel's gate measures against. Reported by the
    /// capture daemon as it learns, and persisted so a restart does not begin from nothing.
    /// </summary>
    public double? NoiseFloorDbfs { get; set; }

    /// <summary>
    /// Whether capture keeps re-learning this channel's floor, or holds the stored one.
    ///
    /// Adaptive is right almost everywhere: the noise floor moves with the band, the weather and the
    /// receiver's own AGC, and a fixed reference goes deaf or chatty as it drifts. The exception is a
    /// channel whose floor the tracker gets wrong — one carrying near-continuous traffic, where a
    /// floor that creeps up toward the signal eventually squelches the very transmissions it should
    /// be opening for. Pinning it hands that judgement to the operator.
    /// </summary>
    public bool AdaptiveSquelch { get; set; } = true;

    public string? LearnedStateJson { get; set; }

    [NotMapped]
    public ChannelLearnedState? LearnedState
    {
        get => LearnedStateJson is null ? null : JsonSerializer.Deserialize<ChannelLearnedState>(LearnedStateJson);
        set => LearnedStateJson = value is null ? null : JsonSerializer.Serialize(value);
    }

    /// <summary>Reads just the learned mode out of a raw JSON column, for projections that never build the entity.</summary>
    public static DetectedMode? ParseLearnedMode(string? learnedStateJson) =>
        learnedStateJson is null ? null : JsonSerializer.Deserialize<ChannelLearnedState>(learnedStateJson)?.Mode;

    public ICollection<Transmission> Transmissions { get; set; } = [];

    public void Configure(EntityTypeBuilder<Channel> builder)
    {
        builder.HasIndex(c => c.FrequencyHz).IsUnique();
        builder.Property(c => c.Label).HasMaxLength(128);
        builder.Property(c => c.Callsign).HasMaxLength(16);
        builder.Property(c => c.Description).HasMaxLength(512);
        builder.Property(c => c.AutoDisabledReason).HasMaxLength(256);
    }
}

/// <summary>Per-channel learned state, stored as a JSON column on <see cref="Channel"/>.</summary>
public class ChannelLearnedState
{
    public double? CourtesyToneHz { get; set; }

    public double? CtcssToneHz { get; set; }

    /// <summary>DCS code observed under this channel's traffic, as the octal number operators quote.</summary>
    public int? DcsCode { get; set; }

    /// <summary>Repeater squelch-tail duration observed after user unkey, milliseconds.</summary>
    public int? TailMs { get; set; }

    /// <summary>
    /// Modulation most recently measured on this channel. This is what lets a frequency be labelled
    /// for what it carries — and what keeps a data or digital channel from being auto-disabled for
    /// never producing speech, since it is now understood rather than merely silent.
    /// </summary>
    public DetectedMode? Mode { get; set; }

    /// <summary>When <see cref="Mode"/> was last observed. A mode that has not been seen in months is stale, not wrong.</summary>
    public DateTime? ModeUpdatedUtc { get; set; }

    public DateTime? UpdatedUtc { get; set; }
}
