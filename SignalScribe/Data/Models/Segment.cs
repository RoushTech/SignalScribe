using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SignalScribe.Data.Models;

/// <summary>A speaker-attributed span of a transmission, produced by segmentation. Carries the transcript and speaker embedding.</summary>
public class Segment : IEntityTypeConfiguration<Segment>
{
    /// <summary>
    /// <see cref="TranscriptionModel"/> value marking a segment as a decoded data frame rather than
    /// speech. Packets share the segment table so they inherit the FTS index and every list view, but
    /// they must stay distinguishable: a beacon is not someone talking, and counting it as speech
    /// would re-enable the very data frequencies the channel audit exists to shut.
    /// </summary>
    public const string PacketDecoderModel = "signalscribe.modem/afsk1200";

    /// <summary>
    /// <see cref="TranscriptionModel"/> value for a D-STAR header. Like a packet it is decoded fact
    /// rather than speech, and must not count toward a channel having ever carried a voice.
    /// </summary>
    public const string DStarHeaderModel = "signalscribe/dstar-header";

    /// <summary>Suffix marking a model name as a header decoder rather than a transcriber.</summary>
    private const string HeaderModelSuffix = "-header";

    /// <summary>
    /// The model name for a mode's header decoder. Generated from the mode rather than listed, so a
    /// new framer needs no constant here — and it reproduces <see cref="DStarHeaderModel"/> exactly,
    /// which keeps rows written before there was more than one digital mode valid.
    /// </summary>
    public static string HeaderModel(Enums.DetectedMode mode) =>
        $"signalscribe/{mode.ToString().ToLowerInvariant()}{HeaderModelSuffix}";

    /// <summary>Whether a model name belongs to any mode's header decoder.</summary>
    public static bool IsHeaderModel(string? transcriptionModel) =>
        transcriptionModel is not null
        && transcriptionModel.StartsWith("signalscribe/", StringComparison.Ordinal)
        && transcriptionModel.EndsWith(HeaderModelSuffix, StringComparison.Ordinal);

    /// <summary>
    /// Whether a segment came from a decoder rather than from listening to someone talk. Kept as one
    /// predicate so every place that asks "was there speech here" stays consistent — the channel
    /// audit in particular, where getting it wrong re-enables the data frequencies it exists to shut.
    /// </summary>
    public static bool IsDecodedData(string? transcriptionModel) =>
        transcriptionModel == PacketDecoderModel || IsHeaderModel(transcriptionModel);

    public long Id { get; set; }

    public long TransmissionId { get; set; }

    public Transmission Transmission { get; set; } = null!;

    public int StartMs { get; set; }

    public int EndMs { get; set; }

    public string? Transcript { get; set; }

    /// <summary>Model name+version that produced the transcript, e.g. "whisper.cpp/small.en-q5_1". Audio is ground truth; results are reproducible.</summary>
    public string? TranscriptionModel { get; set; }

    public byte[]? SpeakerEmbedding { get; set; }

    public string? EmbeddingModel { get; set; }

    /// <summary>Callsign extracted from this segment's transcript, normalized (e.g. "KD9ABC").</summary>
    public string? Callsign { get; set; }

    public long? SpeakerId { get; set; }

    public Speaker? Speaker { get; set; }

    /// <summary>Every field the mode's header carried, as JSON. Null for speech.</summary>
    public string? HeaderFieldsJson { get; set; }

    /// <summary>
    /// The decoded header in full, typed. <see cref="Transcript"/> holds only the one-line reading
    /// that lists and search need; this is the whole record, because a digital header is the only
    /// thing a mode with no vocoder tells us and throwing away the fields it does carry — routing,
    /// call type, emergency flag — would be discarding the entire content of the transmission.
    /// </summary>
    [NotMapped]
    public IReadOnlyList<Contracts.HeaderField>? HeaderFields
    {
        get => HeaderFieldsJson is null
            ? null
            : JsonSerializer.Deserialize<List<Contracts.HeaderField>>(HeaderFieldsJson);
        set => HeaderFieldsJson = value is null ? null : JsonSerializer.Serialize(value);
    }

    public void Configure(EntityTypeBuilder<Segment> builder)
    {
        builder.HasIndex(s => s.TransmissionId);
        builder.HasIndex(s => s.Callsign);
        builder.Property(s => s.TranscriptionModel).HasMaxLength(128);
        builder.Property(s => s.EmbeddingModel).HasMaxLength(128);
        builder.Property(s => s.Callsign).HasMaxLength(16);
    }
}
