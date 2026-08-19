using SignalScribe.Capture.Digital.C4fm;
using SignalScribe.Enums;
using Xunit;

namespace SignalScribe.Tests;

public class C4fmSyncDetectorTests
{
    private const double Outer = 1_944;

    private const double Inner = 648;

    /// <summary>BS sourced voice, ETSI TS 102 361-1 table 9.2 — the word every DMR repeater sends.</summary>
    private const ulong DmrBsVoice = 0x755FD7DF75F7;

    /// <summary>P25 Phase 1 frame sync, TIA-102.BAAA.</summary>
    private const ulong P25FrameSync = 0x5575F5FF77FF;

    /// <summary>YSF frame sync — 40 bits, so it exercises the shorter-word path.</summary>
    private const ulong YsfFrameSync = 0xD471C9634D;

    /// <summary>Expands a sync word into its symbols under the shared C4FM mapping, first symbol from the top dibit.</summary>
    private static IEnumerable<double> SyncSymbols(ulong word, int bits)
    {
        for (var d = (bits / 2) - 1; d >= 0; d--)
        {
            var dibit = (int)((word >> (2 * d)) & 0b11);
            // 01 → +3, 00 → +1, 10 → −1, 11 → −3.
            yield return dibit switch
            {
                0b01 => Outer,
                0b00 => Inner,
                0b10 => -Inner,
                _ => -Outer,
            };
        }
    }

    /// <summary>Balanced random four-level payload, the stuff between sync words.</summary>
    private static IEnumerable<double> Payload(int symbols, Random rng)
    {
        for (var i = 0; i < symbols; i++)
        {
            yield return rng.Next(4) switch { 0 => Outer, 1 => Inner, 2 => -Inner, _ => -Outer };
        }
    }

    /// <summary>Payload with a sync word repeating at the given cadence — the shape of any C4FM mode.</summary>
    private static List<double> Stream(ulong word, int bits, int syncs, Random rng, double polarity = 1, double dcHz = 0)
    {
        var stream = new List<double>();
        for (var burst = 0; burst < syncs; burst++)
        {
            stream.AddRange(Payload(120, rng));
            stream.AddRange(SyncSymbols(word, bits));
            stream.AddRange(Payload(120, rng));
        }

        return [.. stream.Select(s => (s * polarity) + dcHz)];
    }

    /// <summary>NXDN TS 1-E §4.4.4: symbols −3,+1,−3,+3,−3,−3,+3,+3,−1,+3 — 20 bits, so it needs five repeats.</summary>
    private const ulong NxdnFrameSync = 0xCDF59;

    [Theory]
    [InlineData(DmrBsVoice, 48, DetectedMode.Dmr, 4)]
    [InlineData(P25FrameSync, 48, DetectedMode.P25Phase1, 4)]
    [InlineData(YsfFrameSync, 40, DetectedMode.Ysf, 4)]
    [InlineData(NxdnFrameSync, 20, DetectedMode.Nxdn, 8)]
    public void EachModesSyncNamesThatModeAndOnlyIt(ulong word, int bits, DetectedMode expected, int repeats)
    {
        var detector = new C4fmSyncDetector();
        foreach (var s in Stream(word, bits, syncs: repeats, new Random(3)))
        {
            detector.Feed(s);
        }

        Assert.Equal(expected, detector.Named);

        // Cross-talk between tables would name the wrong mode with total confidence.
        foreach (var other in new[] { DetectedMode.Dmr, DetectedMode.P25Phase1, DetectedMode.Ysf, DetectedMode.Nxdn }.Where(m => m != expected))
        {
            Assert.True(detector.SyncCount(other) <= 1, $"{other} matched {detector.SyncCount(other)} times on a {expected} stream");
        }
    }

    /// <summary>
    /// A 20-bit word is short enough that a long noise transmission will eventually match it once or
    /// twice; the five-repeat requirement is what keeps that from ever naming a channel. This pins
    /// the requirement with the accident rate the tolerance maths predicts.
    /// </summary>
    [Fact]
    public void ShortNxdnWordNeedsFiveRepeats()
    {
        var detector = new C4fmSyncDetector();
        foreach (var s in Stream(NxdnFrameSync, 20, syncs: 4, new Random(29)))
        {
            detector.Feed(s);
        }

        Assert.Equal(4, detector.SyncCount(DetectedMode.Nxdn));
        Assert.Equal(DetectedMode.Unknown, detector.Named);
    }

    /// <summary>Which way a symbol comes out of the discriminator depends on the receiver's mixing.</summary>
    [Fact]
    public void InvertedPolarityIsStillDetected()
    {
        var detector = new C4fmSyncDetector();
        foreach (var s in Stream(DmrBsVoice, 48, syncs: 4, new Random(5), polarity: -1))
        {
            detector.Feed(s);
        }

        Assert.Equal(DetectedMode.Dmr, detector.Named);
    }

    /// <summary>
    /// The case that produced this detector: 144.980 sits 5 kHz off its filterbank bin, and the
    /// demodulator's carrier tracking cannot be relied on mid-burst. The detector tracks its own DC,
    /// like every framer (CLAUDE.md).
    /// </summary>
    [Fact]
    public void CarrierOffsetDoesNotBlindTheDetector()
    {
        var detector = new C4fmSyncDetector();
        foreach (var s in Stream(DmrBsVoice, 48, syncs: 6, new Random(7), dcHz: 2_500))
        {
            detector.Feed(s);
        }

        Assert.Equal(DetectedMode.Dmr, detector.Named);
    }

    [Fact]
    public void SlicerNoiseInsideToleranceStillMatches()
    {
        var rng = new Random(11);
        var detector = new C4fmSyncDetector();
        foreach (var s in Stream(P25FrameSync, 48, syncs: 6, rng))
        {
            // ±350 Hz of symbol noise: enough to occasionally cross the magnitude threshold,
            // nowhere near enough to flip signs.
            detector.Feed(s + ((rng.NextDouble() * 700) - 350));
        }

        Assert.Equal(DetectedMode.P25Phase1, detector.Named);
    }

    /// <summary>One accidental match must never name a channel — only repetition may.</summary>
    [Fact]
    public void OneSyncIsNotAVerdict()
    {
        var detector = new C4fmSyncDetector();
        foreach (var s in Stream(DmrBsVoice, 48, syncs: 1, new Random(13)))
        {
            detector.Feed(s);
        }

        Assert.Equal(1, detector.SyncCount(DetectedMode.Dmr));
        Assert.Equal(DetectedMode.Unknown, detector.Named);
    }

    [Fact]
    public void NoiseAndVoiceNeverSync()
    {
        var rng = new Random(17);

        var onNoise = new C4fmSyncDetector();
        for (var i = 0; i < 50_000; i++)
        {
            onNoise.Feed((rng.NextDouble() * 8_000) - 4_000); // squelch-open discriminator noise
        }

        var onVoice = new C4fmSyncDetector();
        for (var i = 0; i < 50_000; i++)
        {
            var t = i / 4_800.0;
            onVoice.Feed(2_000 * Math.Sin(2 * Math.PI * 190 * t) * (0.5 + (0.5 * Math.Sin(2 * Math.PI * 4 * t))));
        }

        Assert.Equal(DetectedMode.Unknown, onNoise.Named);
        Assert.Equal(DetectedMode.Unknown, onVoice.Named);
    }

    /// <summary>D-STAR shares the symbol stream; its bit patterns must not read as C4FM sync.</summary>
    [Fact]
    public void TwoLevelDStarSymbolsDoNotSync()
    {
        var rng = new Random(19);
        var detector = new C4fmSyncDetector();
        for (var i = 0; i < 50_000; i++)
        {
            detector.Feed(rng.Next(2) == 0 ? 1_200 : -1_200);
        }

        Assert.Equal(DetectedMode.Unknown, detector.Named);
    }
}
