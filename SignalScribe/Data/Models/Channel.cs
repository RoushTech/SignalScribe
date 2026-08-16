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

    public string? Notes { get; set; }

    /// <summary>Adaptive squelch reference, dBFS. Persisted so the capture daemon survives restarts.</summary>
    public double? NoiseFloorDbfs { get; set; }

    public string? LearnedStateJson { get; set; }

    [NotMapped]
    public ChannelLearnedState? LearnedState
    {
        get => LearnedStateJson is null ? null : JsonSerializer.Deserialize<ChannelLearnedState>(LearnedStateJson);
        set => LearnedStateJson = value is null ? null : JsonSerializer.Serialize(value);
    }

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

    /// <summary>Repeater squelch-tail duration observed after user unkey, milliseconds.</summary>
    public int? TailMs { get; set; }

    public DateTime? UpdatedUtc { get; set; }
}
