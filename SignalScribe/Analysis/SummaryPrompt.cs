using SignalScribe.Contracts;

namespace SignalScribe.Analysis;

/// <summary>
/// Builds the narrative-summary prompt. Kept out of the worker so the wording — which is the whole
/// behaviour — can be tested without loading several gigabytes of weights.
///
/// The facts are authoritative and the model only writes prose from them (CLAUDE.md). What that
/// leaves the prompt responsible for is *how much* prose, and a fixed "concise, 3-6 sentences" got
/// that wrong in the direction nobody notices: measured on air, a 41-transmission session with 8,700
/// characters of transcript came back as two sentences — a 35:1 compression that threw away almost
/// everything that was said. A small model told to be concise will always err short, so the
/// instruction has to scale with the material in front of it.
/// </summary>
public static class SummaryPrompt
{
    /// <summary>
    /// How long the summary should be, given how much was actually said. Expressed in sentences
    /// rather than words because a 1.5B model follows sentence counts far more reliably.
    /// </summary>
    public static string LengthGuidance(int transcriptChars) => transcriptChars switch
    {
        < 500 => "1-2 sentences",
        < 2_000 => "3-5 sentences",
        < 6_000 => "6-9 sentences",
        _ => "two paragraphs of 5-8 sentences each",
    };

    /// <summary>Tokens to allow for the answer, with headroom over the requested length.</summary>
    public static int MaxTokens(int transcriptChars) => transcriptChars < 2_000 ? 400 : 900;

    /// <summary>
    /// Transcript characters to keep. Bounded so the prompt cannot crowd out the model's context
    /// window — at roughly four characters per token this leaves room for the instructions and the
    /// answer inside 8192.
    /// </summary>
    public const int MaxTranscriptChars = 20_000;

    public static string Build(SessionFacts facts)
    {
        var transcript = facts.Transcript.Length > MaxTranscriptChars
            ? facts.Transcript[..MaxTranscriptChars] + "\n[transcript truncated]"
            : facts.Transcript;

        var kind = facts.IsNet
            ? $"an amateur radio net{(facts.NetName is null ? "" : $" (\"{facts.NetName}\")")}"
            : "an amateur radio conversation";
        var roster = facts.Callsigns.Count > 0 ? string.Join(", ", facts.Callsigns) : "none identified";
        var duration = facts.EndUtc is null
            ? "unknown"
            : $"{(facts.EndUtc.Value - facts.StartUtc).TotalMinutes:F0} minutes";

        // Naming what to cover matters as much as the length: asked only for "a summary", the model
        // returns a sentence about the topics and stops. Asked for the subjects discussed, how the
        // conversation moved and anything notable, it actually works through the transcript.
        return $"""
            <|im_start|>system
            You summarize amateur radio activity logs. Write a flowing narrative summary of the session below, {LengthGuidance(transcript.Length)}.

            Cover: what the operators actually talked about, in the order it came up; any specifics worth keeping (places, equipment, weather, plans, times); and how the conversation opened and closed. Prefer concrete detail over general statements — "discussed antenna repairs after the storm" rather than "discussed various topics".

            The transcript is machine-generated from noisy FM audio and contains errors. Where a word is garbled, describe what was meant if it is clear from context and otherwise leave it out. Do not invent callsigns, names, or events that are not in the transcript. Do not mention the transcript, the audio quality, or yourself.<|im_end|>
            <|im_start|>user
            Session: {kind} on {facts.ChannelLabel} ({facts.FrequencyHz / 1_000_000.0:F4} MHz)
            Start: {facts.StartUtc:yyyy-MM-dd HH:mm} UTC, duration: {duration}
            Transmissions: {facts.TransmissionCount}
            Callsigns heard: {roster}

            Transcript:
            {transcript}<|im_end|>
            <|im_start|>assistant
            """;
    }
}
