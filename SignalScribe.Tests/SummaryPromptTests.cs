using SignalScribe.Analysis;
using SignalScribe.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

/// <summary>
/// The summary prompt. Its wording is the behaviour, and the failure it guards is quiet: a small
/// model told to be "concise" returns two sentences for anything, so a long session's content is
/// thrown away with nothing to show that it happened.
/// </summary>
public class SummaryPromptTests(ITestOutputHelper output)
{
    [Fact]
    public void LengthAsksForMoreWhenThereIsMoreToSay()
    {
        var brief = SummaryPrompt.LengthGuidance(300);
        var medium = SummaryPrompt.LengthGuidance(3_000);
        var long_ = SummaryPrompt.LengthGuidance(8_696); // the session that came back as two sentences

        output.WriteLine($"  300 chars → {brief}");
        output.WriteLine($"  3,000 chars → {medium}");
        output.WriteLine($"  8,696 chars → {long_}");

        Assert.NotEqual(brief, medium);
        Assert.NotEqual(medium, long_);
        Assert.Contains("paragraph", long_);
    }

    [Fact]
    public void TokenBudgetGrowsWithTheRequestedLength()
    {
        // A longer ask with the old fixed ceiling would simply be cut off mid-sentence.
        Assert.True(SummaryPrompt.MaxTokens(8_696) > SummaryPrompt.MaxTokens(300));
    }

    [Fact]
    public void TheRealSessionAsksForParagraphsNotSentences()
    {
        var prompt = SummaryPrompt.Build(Facts(transcriptChars: 8_696, transmissions: 41));

        Assert.Contains("two paragraphs", prompt);
        output.WriteLine($"  prompt is {prompt.Length} chars for an 8,696-char transcript");
    }

    [Fact]
    public void TheTranscriptIsBoundedSoItCannotCrowdOutTheContext()
    {
        var prompt = SummaryPrompt.Build(Facts(transcriptChars: 60_000, transmissions: 300));

        Assert.Contains("[transcript truncated]", prompt);
        // Four characters per token against an 8192 context, with room for instructions and answer.
        Assert.True(prompt.Length < 25_000, $"prompt of {prompt.Length} chars risks overrunning the context");
    }

    [Fact]
    public void ANetIsIntroducedAsANetAndCarriesItsName()
    {
        var prompt = SummaryPrompt.Build(Facts(1_000, 20) with { IsNet = true, NetName = "Lunch Bunch" });

        Assert.Contains("amateur radio net", prompt);
        Assert.Contains("Lunch Bunch", prompt);
    }

    [Fact]
    public void AnEmptyRosterSaysSoRatherThanShowingNothing()
    {
        var prompt = SummaryPrompt.Build(Facts(1_000, 20));

        Assert.Contains("none identified", prompt);
    }

    /// <summary>
    /// The transcripts are noisy, and the model must be told to work around that rather than
    /// narrate it — an operator does not want a paragraph about the audio quality.
    /// </summary>
    [Fact]
    public void TheModelIsToldNotToNarrateTheTranscriptItself()
    {
        var prompt = SummaryPrompt.Build(Facts(3_000, 30));

        Assert.Contains("Do not mention the transcript", prompt);
        Assert.Contains("Do not invent callsigns", prompt);
    }

    [Fact]
    public void ConcreteDetailIsAskedForByExample()
    {
        // "various topics" is exactly what the old prompt produced; the example is what steers away.
        Assert.Contains("rather than", SummaryPrompt.Build(Facts(3_000, 30)));
    }

    private static SessionFacts Facts(int transcriptChars, int transmissions) => new(
        SessionId: 5325,
        ChannelLabel: "144.9200 MHz",
        FrequencyHz: 144_920_000,
        IsNet: false,
        NetName: null,
        StartUtc: new DateTime(2026, 8, 19, 3, 21, 39, DateTimeKind.Utc),
        EndUtc: new DateTime(2026, 8, 19, 3, 49, 20, DateTimeKind.Utc),
        TransmissionCount: transmissions,
        Callsigns: [],
        Transcript: new string('x', transcriptChars));
}
