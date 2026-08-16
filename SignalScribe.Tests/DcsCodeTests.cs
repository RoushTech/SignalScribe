using SignalScribe.Capture.Dsp;
using Xunit;

namespace SignalScribe.Tests;

public class DcsCodeTests
{
    [Fact]
    public void EveryStandardCodeSurvivesARoundTrip()
    {
        foreach (var code in DcsCodes.Standard)
        {
            var word = DcsCodes.Encode(code);
            Assert.Equal(code, DcsCodes.Decode(word));
            Assert.True(word < 1 << 23, $"code {code:D3} produced more than 23 bits");
        }
    }

    [Fact]
    public void CorruptedWordsAreRejectedRatherThanGuessed()
    {
        var word = DcsCodes.Encode(023);
        for (var bit = 0; bit < 23; bit++)
        {
            // One flipped bit must not silently decode as some other valid code.
            var decoded = DcsCodes.Decode(word ^ (1 << bit));
            Assert.True(decoded is null or 023, $"flipping bit {bit} decoded as {decoded}");
        }
    }

    [Fact]
    public void TheFixedTailIsWhatSeparatesDcsFromNoise()
    {
        // Parity alone is not enough: the three fixed bits must be present too.
        var word = DcsCodes.Encode(754);
        Assert.NotNull(DcsCodes.Decode(word));
        Assert.Null(DcsCodes.Decode(word ^ (0b111 << 9)));
    }
}
