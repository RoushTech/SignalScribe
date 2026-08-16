using SignalScribe.Analysis;
using Xunit;

namespace SignalScribe.Tests;

public class PhoneticCallsignNormalizerTests
{
    [Fact]
    public void ExtractsItuPhoneticCallsign()
    {
        var hits = PhoneticCallsignNormalizer.ExtractCallsigns(
            "good evening everyone this is kilo delta nine alpha bravo charlie checking in");
        Assert.Equal(["KD9ABC"], hits);
    }

    [Fact]
    public void HandlesNinerAndSpellingVariants()
    {
        var hits = PhoneticCallsignNormalizer.ExtractCallsigns(
            "whiskey niner x-ray yankee zulu for the net");
        Assert.Equal(["W9XYZ"], hits);
    }

    [Fact]
    public void ExtractsMultipleCallsigns()
    {
        var hits = PhoneticCallsignNormalizer.ExtractCallsigns(
            "november zero charlie alfa lima this is kilo delta nine alpha bravo charlie go ahead");
        Assert.Equal(["N0CAL", "KD9ABC"], hits);
    }

    [Fact]
    public void IgnoresNonCallsignPhoneticRuns()
    {
        // A run that doesn't collapse to a valid callsign shape is not a hit.
        var hits = PhoneticCallsignNormalizer.ExtractCallsigns("alpha bravo charlie delta echo");
        Assert.Empty(hits);
    }

    [Fact]
    public void IgnoresShortRuns()
    {
        // "kilo delta nine" alone (3 tokens) is below the run threshold — avoids false hits on stray phonetics.
        var hits = PhoneticCallsignNormalizer.ExtractCallsigns("kilo delta nine and then some talk");
        Assert.Empty(hits);
    }

    [Theory]
    [InlineData("KD9ABC", true)]
    [InlineData("W1AW", true)]
    [InlineData("n0cal", true)]
    [InlineData("HELLO", false)]
    [InlineData("73", false)]
    public void IsCallsignMatchesShape(string text, bool expected)
    {
        Assert.Equal(expected, PhoneticCallsignNormalizer.IsCallsign(text));
    }
}
