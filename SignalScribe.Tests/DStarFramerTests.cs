using System.Text;
using SignalScribe.Capture.Digital.DStar;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

/// <summary>
/// Finding a header in a symbol stream: sync detection, polarity resolution, and the CRC as the
/// thing that decides what is real.
/// </summary>
public class DStarFramerTests(ITestOutputHelper output)
{
    [Fact]
    public void FindsAHeaderAfterAPreamble()
    {
        var found = Run(Transmission("KD9ABC  ", "CQCQCQ  ", invert: false));

        var header = Assert.Single(found);
        Assert.Equal("KD9ABC", header.Mycall);
        output.WriteLine($"  {header.MycallWithSuffix} → {header.Urcall}");
    }

    [Fact]
    public void FindsAHeaderWhenTheDiscriminatorIsInverted()
    {
        // Which way round a one comes out depends on the receiver's mixing, and nothing upstream
        // pins it. A decoder that only handled one polarity would work or not by luck of hardware.
        var found = Run(Transmission("KD9ABC  ", "CQCQCQ  ", invert: true));

        Assert.Equal("KD9ABC", Assert.Single(found).Mycall);
    }

    [Fact]
    public void FindsAHeaderThroughACorruptedSyncPattern()
    {
        var stream = Transmission("W9XYZ   ", "CQCQCQ  ", invert: false);

        // Two sync bits wrong — a header at the edge of copy is worth more than the false starts
        // this tolerance admits, because the CRC still has to agree.
        var syncAt = PreambleSymbols;
        stream[syncAt + 3] = -stream[syncAt + 3];
        stream[syncAt + 9] = -stream[syncAt + 9];

        Assert.Equal("W9XYZ", Assert.Single(Run(stream)).Mycall);
    }

    [Fact]
    public void FindsNothingInNoise()
    {
        var rng = new Random(77);
        var noise = new double[40_000];
        for (var i = 0; i < noise.Length; i++)
        {
            noise[i] = (rng.NextDouble() * 2) - 1;
        }

        var framer = new DStarFramer();
        var found = new List<DStarHeader>();
        framer.HeaderDecoded += found.Add;
        foreach (var s in noise)
        {
            framer.Feed(s);
        }

        // Sync will trip occasionally on 40,000 random symbols — that is expected and harmless.
        // What must never happen is a header coming out of it.
        output.WriteLine($"  {framer.SyncCount} false syncs, {framer.HeaderCount} headers");
        Assert.Empty(found);
    }

    [Fact]
    public void ReadsTwoHeadersInOneStream()
    {
        // A repeater re-sends the header; each should be reported.
        var first = Transmission("KD9ABC  ", "CQCQCQ  ", invert: false);
        var second = Transmission("W9XYZ   ", "CQCQCQ  ", invert: false);
        var combined = new double[first.Length + second.Length];
        first.CopyTo(combined, 0);
        second.CopyTo(combined, first.Length);

        var found = Run(combined);

        Assert.Equal(2, found.Count);
        Assert.Equal(["KD9ABC", "W9XYZ"], found.Select(h => h.Mycall));
    }

    [Fact]
    public void ATruncatedHeaderYieldsNothing()
    {
        var stream = Transmission("KD9ABC  ", "CQCQCQ  ", invert: false);
        Assert.Empty(Run(stream[..(stream.Length - 200)]));
    }

    private const int PreambleSymbols = 64;

    /// <summary>
    /// The marginal-signal path: no header survives, but the every-21-frames voice sync repeats on
    /// its 420 ms schedule for as long as the carrier stands, and that cadence is what names a weak
    /// D-STAR carrier at all.
    /// </summary>
    [Fact]
    public void VoiceFrameSyncsOnCadenceNameTheModeWithoutAHeader()
    {
        var rng = new Random(31);
        var framer = new DStarFramer();
        for (var period = 0; period < 4; period++)
        {
            FeedPayload(framer, rng, DStarFramer.SyncPeriodSymbols - 24);
            FeedVoiceSync(framer, inverted: false);
        }

        Assert.True(framer.VoiceFramesSeen, $"cadenced run only reached {framer.CadencedFrameSyncs}");
        Assert.Equal(0, framer.HeaderCount);
    }

    /// <summary>
    /// A fade can eat a sync or two, and the receiver's clock is not the transmitter's — a real
    /// chain survives a skipped period and a couple of symbols of timing slip.
    /// </summary>
    [Fact]
    public void ACadenceChainSurvivesAMissedSyncAndTimingSlip()
    {
        var rng = new Random(41);
        var framer = new DStarFramer();
        FeedPayload(framer, rng, 500);
        FeedVoiceSync(framer, inverted: false);
        FeedPayload(framer, rng, DStarFramer.SyncPeriodSymbols - 24 + 2); // one period, two symbols late
        FeedVoiceSync(framer, inverted: false);
        FeedPayload(framer, rng, (2 * DStarFramer.SyncPeriodSymbols) - 24 - 2); // a missed sync, then early
        FeedVoiceSync(framer, inverted: false);

        Assert.True(framer.VoiceFramesSeen, $"cadenced run only reached {framer.CadencedFrameSyncs}");
    }

    /// <summary>
    /// Three matches at arbitrary spacing is what accidental hits on analog traffic look like, and
    /// exactly what used to name an FM ragchew D-STAR and silence its transcription. The count is
    /// there; the schedule is not.
    /// </summary>
    [Fact]
    public void VoiceFrameSyncsOffCadenceDoNotNameTheMode()
    {
        var rng = new Random(31);
        var framer = new DStarFramer();
        for (var i = 0; i < 5; i++)
        {
            FeedPayload(framer, rng, 300); // nothing like the 2016-symbol sync period
            FeedVoiceSync(framer, inverted: false);
        }

        Assert.True(framer.FrameSyncCount >= DStarFramer.MinFrameSyncs, $"only {framer.FrameSyncCount} raw hits — the trap is not armed");
        Assert.False(framer.VoiceFramesSeen, $"off-cadence hits formed a run of {framer.CadencedFrameSyncs}");
    }

    /// <summary>
    /// A transmission's polarity is fixed by the receiver's mixing; accidental matches flip a coin
    /// on it. Perfectly cadenced hits that keep changing polarity are not one transmission.
    /// </summary>
    [Fact]
    public void CadencedSyncsInMixedPolarityDoNotNameTheMode()
    {
        // Alternating filler rather than random: it provably never matches the sync (see
        // APreambleAloneIsNotAVoiceFrameSync), so exactly the three deliberate syncs register and
        // the polarity rule is what the assertion isolates.
        var framer = new DStarFramer();
        for (var period = 0; period < 3; period++)
        {
            for (var i = 0; i < DStarFramer.SyncPeriodSymbols - 24; i++)
            {
                framer.Feed(i % 2 == 0 ? 1 : -1);
            }

            FeedVoiceSync(framer, inverted: period % 2 == 1);
        }

        Assert.Equal(3, framer.FrameSyncCount);
        Assert.False(framer.VoiceFramesSeen, $"mixed-polarity hits formed a run of {framer.CadencedFrameSyncs}");
    }

    /// <summary>
    /// The voice sync's first ten bits are the same alternation as the bit-sync preamble, so a long
    /// preamble is the natural false-positive candidate. The M-sequence half is what must save it.
    /// </summary>
    [Fact]
    public void APreambleAloneIsNotAVoiceFrameSync()
    {
        var framer = new DStarFramer();
        for (var i = 0; i < 2_000; i++)
        {
            framer.Feed(i % 2 == 0 ? 1 : -1);
        }

        Assert.Equal(0, framer.FrameSyncCount);
    }

    /// <summary>
    /// Ninety seconds of noise — a latched gate, or the hang-time gaps of a long conversation. The
    /// 24-bit sync at two errors' tolerance in both polarities trips about every six seconds on
    /// this, so the raw count sails past three; naming the mode from it is what put an analog
    /// channel into the digital-voice bucket. The cadence requirement is what must hold the line.
    /// </summary>
    [Fact]
    public void NoiseAccumulatesRawSyncsButNeverACadencedRun()
    {
        var rng = new Random(37);
        var framer = new DStarFramer();
        for (var i = 0; i < 432_000; i++)
        {
            framer.Feed((rng.NextDouble() * 2) - 1);
        }

        output.WriteLine($"  {framer.FrameSyncCount} raw hits, longest cadenced run {framer.CadencedFrameSyncs}");
        Assert.True(framer.FrameSyncCount >= DStarFramer.MinFrameSyncs, $"only {framer.FrameSyncCount} raw hits — the trap is not armed");
        Assert.False(framer.VoiceFramesSeen, $"noise formed a cadenced run of {framer.CadencedFrameSyncs}");
    }

    private static void FeedPayload(DStarFramer framer, Random rng, int symbols)
    {
        for (var i = 0; i < symbols; i++)
        {
            framer.Feed(rng.Next(2) == 0 ? 1 : -1);
        }
    }

    private static void FeedVoiceSync(DStarFramer framer, bool inverted)
    {
        for (var i = 23; i >= 0; i--)
        {
            var bit = ((DStarFramer.VoiceFrameSync >> i) & 1) == 1;
            framer.Feed(bit != inverted ? 1 : -1);
        }
    }

    private static List<DStarHeader> Run(double[] symbols)
    {
        var framer = new DStarFramer();
        var found = new List<DStarHeader>();
        framer.HeaderDecoded += found.Add;
        foreach (var s in symbols)
        {
            framer.Feed(s);
        }

        return found;
    }

    /// <summary>Bit-sync preamble, frame sync, then the coded header — what a radio actually emits.</summary>
    private static double[] Transmission(string mycall, string urcall, bool invert)
    {
        var header = BuildHeader(mycall, urcall);
        var coded = new byte[DStarHeaderFec.ChannelBits];
        Assert.True(DStarHeaderFec.TryEncode(header, coded));

        var symbols = new List<double>(PreambleSymbols + 24 + coded.Length + 32);

        // Alternating bits for the clock recovery to catch.
        for (var i = 0; i < PreambleSymbols; i++)
        {
            symbols.Add(i % 2 == 0 ? 1 : -1);
        }

        for (var i = 23; i >= 0; i--)
        {
            symbols.Add(((DStarFramer.HeaderFrameSync >> i) & 1) == 1 ? 1 : -1);
        }

        foreach (var bit in coded)
        {
            symbols.Add(bit == 1 ? 1 : -1);
        }

        // Trailing traffic, so nothing depends on the stream ending neatly at the header.
        var rng = new Random(5);
        for (var i = 0; i < 32; i++)
        {
            symbols.Add(rng.Next(2) == 0 ? 1 : -1);
        }

        var result = symbols.ToArray();
        if (invert)
        {
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = -result[i];
            }
        }

        return result;
    }

    private static byte[] BuildHeader(string mycall, string urcall)
    {
        var header = new byte[DStarHeader.Length];
        Write(header.AsSpan(3, 8), "W9XYZ  G");
        Write(header.AsSpan(11, 8), "W9XYZ  B");
        Write(header.AsSpan(19, 8), urcall);
        Write(header.AsSpan(27, 8), mycall);
        Write(header.AsSpan(35, 4), "    ");
        DStarHeader.StampCrc(header);
        return header;
    }

    private static void Write(Span<byte> field, string text)
    {
        field.Fill((byte)' ');
        Encoding.ASCII.GetBytes(text.PadRight(field.Length)[..field.Length], field);
    }
}
