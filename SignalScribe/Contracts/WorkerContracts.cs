namespace SignalScribe.Contracts;

// api/internal shapes for the processing workers (transcription, embeddings, summaries).

public record TransmissionInfo(
    long Id,
    long FrequencyHz,
    string AudioPath,
    bool IsDouble,
    List<MarkerInfo> Markers);

public record MarkerInfo(SignalScribe.Enums.MarkerType Type, int OffsetMs);

public record TranscriptSegmentIngest(int StartMs, int EndMs, string Text);

public record TranscriptIngest(long TransmissionId, string Model, List<TranscriptSegmentIngest> Segments);

/// <summary>Deterministic facts for a session — computed by code over the database, never by the LLM (CLAUDE.md).</summary>
public record SessionFacts(
    long SessionId,
    string ChannelLabel,
    long FrequencyHz,
    bool IsNet,
    string? NetName,
    DateTime StartUtc,
    DateTime? EndUtc,
    int TransmissionCount,
    List<string> Callsigns,
    string Transcript);

public record SessionSummaryIngest(string Model, string Summary);
