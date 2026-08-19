using SignalScribe.Analysis;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

/// <summary>
/// The human reading of a decoded packet. The raw TNC2 frame stays the record; this is what makes a
/// row in the transmission list mean something without knowing APRS syntax.
/// </summary>
public class AprsSummaryTests(ITestOutputHelper output)
{
    [Fact]
    public void ReadsAPositionBeacon()
    {
        var summary = Describe("KD9ABC-7>APRS,WIDE1-1,WIDE2-2:!4221.55N/08750.12W#PHG5130 test beacon");

        Assert.NotNull(summary);
        Assert.Contains("°N", summary);
        Assert.Contains("°W", summary);
        Assert.Contains("PHG5130 test beacon", summary);
    }

    [Fact]
    public void ReadsAMessageAsWhoItIsForAndWhatItSays()
    {
        var summary = Describe("KD9ABC>APRS::W9XYZ    :are you on the repeater{01");

        Assert.NotNull(summary);
        Assert.Contains("W9XYZ", summary);
        Assert.Contains("are you on the repeater", summary);
    }

    [Fact]
    public void ReadsAStatusReport()
    {
        Assert.Equal("monitoring 146.520", Describe("KD9ABC>APRS:>monitoring 146.520"));
    }

    [Fact]
    public void ReadsWeatherFiguresRatherThanTheRawField()
    {
        var summary = Describe("KD9ABC>APRS:!4221.55N/08750.12W_180/010g015t078r000p000h55b10133");

        Assert.NotNull(summary);
        output.WriteLine($"  {summary}");
        Assert.Contains("78", summary);   // temperature in F
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("this is not a packet at all")]
    [InlineData("KD9ABC>APRS:")]
    public void SaysNothingRatherThanGuessingWhenItCannotRead(string? tnc2)
    {
        // A frame we cannot parse is not an error. The raw TNC2 line is still displayed and still
        // searchable — inventing a reading for it would be worse than staying quiet.
        Assert.Null(AprsSummary.Describe(tnc2));
    }

    [Fact]
    public void DoesNotThrowOnMalformedTraffic()
    {
        // The APRS information field is loose and much abused; real air carries plenty the parser
        // chokes on, and a decoded packet must never take down the ingest path.
        foreach (var raw in new[]
        {
            "KD9ABC>APRS:!invalid",
            "KD9ABC>APRS:;object   *111111z",
            "KD9ABC>APRS:}third>party:junk",
            ">",
            "A>B:!0000.00N/00000.00W#",
        })
        {
            var summary = AprsSummary.Describe(raw);
            output.WriteLine($"  {raw,-45} -> {summary ?? "(nothing)"}");
        }
    }

    private string? Describe(string tnc2)
    {
        var summary = AprsSummary.Describe(tnc2);
        output.WriteLine($"  {tnc2}\n    -> {summary ?? "(nothing)"}");
        return summary;
    }
}
