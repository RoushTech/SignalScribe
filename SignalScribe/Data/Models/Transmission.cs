using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SignalScribe.Data.Models;

/// <summary>One squelch-open recording clip. Segmentation into speaker-attributed spans happens downstream (see <see cref="Segment"/>).</summary>
public class Transmission : IEntityTypeConfiguration<Transmission>
{
    public long Id { get; set; }

    public int ChannelId { get; set; }

    public Channel Channel { get; set; } = null!;

    public DateTime StartUtc { get; set; }

    public DateTime? EndUtc { get; set; }

    /// <summary>Path to the Opus/OGG clip, relative to the audio root.</summary>
    public string AudioPath { get; set; } = string.Empty;

    public double PeakDbfs { get; set; }

    /// <summary>Mean FM discriminator DC offset — transmitter frequency-error fingerprint (simplex only; meaningless on repeater outputs).</summary>
    public double? MeanCarrierOffsetHz { get; set; }

    /// <summary>Heterodyne detected — two stations keyed simultaneously. Not worth transcribing.</summary>
    public bool IsDouble { get; set; }

    /// <summary>Milliseconds of speech-band audio measured at capture. Below the transcription threshold this clip is kept but never sent to Whisper.</summary>
    public int VoicedMs { get; set; }

    public long? SessionId { get; set; }

    public Session? Session { get; set; }

    /// <summary>CTCSS tone measured under the audio, in Hz. Null when there was none, or too little audio to tell.</summary>
    public double? CtcssHz { get; set; }

    /// <summary>DCS code decoded from the sub-audible bitstream, as the octal number operators quote.</summary>
    public int? DcsCode { get; set; }

    /// <summary>Model that last transcribed this clip, e.g. "whisper.net/ggml-small.en-q5_1.bin". Set even when the run found no speech, so "not yet processed" and "processed, nothing said" stay distinguishable.</summary>
    public string? TranscribedByModel { get; set; }

    public ICollection<Marker> Markers { get; set; } = [];

    public ICollection<Segment> Segments { get; set; } = [];

    public void Configure(EntityTypeBuilder<Transmission> builder)
    {
        // Idempotent ingest: the capture daemon may replay its spool journal after a host outage.
        builder.HasIndex(t => new { t.ChannelId, t.StartUtc }).IsUnique();
        builder.Property(t => t.TranscribedByModel).HasMaxLength(128);
        builder.Property(t => t.AudioPath).HasMaxLength(512);
    }
}
