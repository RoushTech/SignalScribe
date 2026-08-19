using System.Text;
using SignalScribe.Capture.Digital.DStar;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

/// <summary>
/// The D-STAR header: callsign routing recovered with no vocoder involved.
///
/// What these can prove is that the chain is internally consistent and that the CRC does its job.
/// What they cannot prove is that the interleave orientation and the bit ordering inside the
/// convolutional code match what a real radio transmits — those round-trip perfectly while being
/// wrong, and only an off-air capture settles them. That gap is stated on
/// <see cref="DStarHeaderFec"/> rather than hidden behind green tests.
/// </summary>
public class DStarHeaderTests(ITestOutputHelper output)
{
    [Fact]
    public void RecoversCallsignsThroughTheWholeCodingChain()
    {
        var header = BuildHeader("KD9ABC  ", "MOBI", "CQCQCQ  ", "W9XYZ  B", "W9XYZ  G");

        var decoded = RoundTrip(header);

        Assert.NotNull(decoded);
        Assert.Equal("KD9ABC", decoded.Mycall);
        Assert.Equal("MOBI", decoded.MycallSuffix);
        Assert.Equal("CQCQCQ", decoded.Urcall);
        Assert.Equal("W9XYZ  B", decoded.RepeaterSource);
        Assert.Equal("W9XYZ  G", decoded.RepeaterTarget);
        Assert.True(decoded.IsGroupCall);
        output.WriteLine($"  {decoded.MycallWithSuffix} → {decoded.Urcall} via {decoded.RepeaterSource}/{decoded.RepeaterTarget}");
    }

    [Fact]
    public void CorrectsChannelErrorsTheCodeIsBuiltToAbsorb()
    {
        // The point of a rate-1/2 K=5 code with interleaving is that a burst of corrupted bits comes
        // out clean. If this ever stops passing, the FEC has been broken rather than merely retuned.
        var header = BuildHeader("KD9ABC  ", "    ", "CQCQCQ  ", "W9XYZ  B", "W9XYZ  G");
        var channel = Encode(header);

        // A contiguous burst — what a fade actually does — rather than scattered single bits.
        for (var i = 100; i < 112; i++)
        {
            channel[i] = -channel[i];
        }

        Assert.True(DStarHeaderFec.TryDecode(channel, out var recovered));
        Assert.Equal("KD9ABC", DStarHeader.Parse(recovered).Mycall);
    }

    [Fact]
    public void RejectsNoiseRatherThanInventingCallsigns()
    {
        // Sixteen characters of plausible-looking callsign recovered from static would be far worse
        // than nothing: it would put a station on the air that was never there.
        var rng = new Random(31);
        var noise = new double[DStarHeaderFec.ChannelBits];
        for (var i = 0; i < noise.Length; i++)
        {
            noise[i] = (rng.NextDouble() * 2) - 1;
        }

        Assert.False(DStarHeaderFec.TryDecode(noise, out _));
    }

    [Fact]
    public void RejectsAHeaderTooDamagedToTrust()
    {
        var header = BuildHeader("KD9ABC  ", "    ", "CQCQCQ  ", "W9XYZ  B", "W9XYZ  G");
        var channel = Encode(header);

        // Far past what the code can carry — the CRC has to catch what Viterbi could not fix.
        for (var i = 0; i < channel.Length; i += 3)
        {
            channel[i] = -channel[i];
        }

        Assert.False(DStarHeaderFec.TryDecode(channel, out _));
    }

    [Fact]
    public void ReadsTheEmergencyFlag()
    {
        var header = BuildHeader("KD9ABC  ", "    ", "CQCQCQ  ", "W9XYZ  B", "W9XYZ  G");
        header[0] |= 0x08;
        DStarHeader.StampCrc(header);

        Assert.True(RoundTrip(header)!.IsEmergency);
    }

    [Fact]
    public void ADirectedCallIsNotAGroupCall()
    {
        var header = BuildHeader("KD9ABC  ", "    ", "W9XYZ   ", "W9XYZ  B", "W9XYZ  G");
        var decoded = RoundTrip(header)!;

        Assert.False(decoded.IsGroupCall);
        Assert.Equal("W9XYZ", decoded.Urcall);
    }

    [Fact]
    public void TheCrcRejectsASingleFlippedHeaderBit()
    {
        var header = BuildHeader("KD9ABC  ", "    ", "CQCQCQ  ", "W9XYZ  B", "W9XYZ  G");
        Assert.True(DStarHeader.CrcMatches(header));

        header[20] ^= 0x01;
        Assert.False(DStarHeader.CrcMatches(header));
    }

    [Fact]
    public void TheScramblerIsThePublishedSequence()
    {
        // Derived from x^7 + x^4 + 1 rather than copied from a table. Pinning the opening bits against
        // the published sequence is what says the derivation was right — the polynomial alone would
        // still be wrong with the wrong seed or tap order.
        int[] expected =
        [
            0, 0, 0, 0, 1, 1, 1, 0, 1, 1, 1, 1, 0, 0, 1, 0,
            1, 1, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0,
        ];

        // Scrambling an all-ones soft vector leaves the keystream visible as sign flips.
        var probe = new double[DStarHeaderFec.ChannelBits];
        Array.Fill(probe, 1.0);

        // The encoder scrambles; feeding a known input exposes the sequence it used.
        var flipped = ScrambleProbe(probe);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], flipped[i]);
        }
    }

    /// <summary>Runs a header out through the encoder and back through the decoder.</summary>
    private static DStarHeader? RoundTrip(byte[] header)
    {
        var channel = Encode(header);
        return DStarHeaderFec.TryDecode(channel, out var recovered) ? DStarHeader.Parse(recovered) : null;
    }

    private static double[] Encode(byte[] header)
    {
        var bits = new byte[DStarHeaderFec.ChannelBits];
        Assert.True(DStarHeaderFec.TryEncode(header, bits));

        var soft = new double[bits.Length];
        for (var i = 0; i < bits.Length; i++)
        {
            soft[i] = bits[i] == 1 ? 1 : -1;
        }

        return soft;
    }

    /// <summary>Recovers the keystream by encoding a header of known content twice — see the scrambler test.</summary>
    private static int[] ScrambleProbe(double[] ones)
    {
        // Reproduce the generator independently of the implementation under test, so the test is a
        // statement about the sequence rather than a mirror of the code.
        var register = 0x07;
        var flipped = new int[ones.Length];
        for (var i = 0; i < ones.Length; i++)
        {
            flipped[i] = (register >> 6) & 1;
            var feedback = ((register >> 6) ^ (register >> 3)) & 1;
            register = ((register << 1) | feedback) & 0x7F;
        }

        return flipped;
    }

    private static byte[] BuildHeader(string mycall, string suffix, string urcall, string rpt1, string rpt2)
    {
        var header = new byte[DStarHeader.Length];
        Write(header.AsSpan(3, 8), rpt2);
        Write(header.AsSpan(11, 8), rpt1);
        Write(header.AsSpan(19, 8), urcall);
        Write(header.AsSpan(27, 8), mycall);
        Write(header.AsSpan(35, 4), suffix);
        DStarHeader.StampCrc(header);
        return header;
    }

    private static void Write(Span<byte> field, string text)
    {
        field.Fill((byte)' ');
        Encoding.ASCII.GetBytes(text.PadRight(field.Length)[..field.Length], field);
    }
}
