using Microsoft.Extensions.Logging.Abstractions;
using SignalScribe.Capture.Dsp;
using SignalScribe.Contracts;
using SignalScribe.Enums;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

/// <summary>
/// The pre-roll buffer: every gate starts recording from before the carrier was noticed.
///
/// The squelch gate waits two blocks for a carrier to persist before opening, which is what keeps
/// clicks and splatter out of the recordings — and the price of that caution is that the opening
/// moment of every transmission is already gone. On analog that is the clipped first syllable; on
/// D-STAR it was the entire header, and with it the callsigns.
/// </summary>
public class PreRollTests(ITestOutputHelper output)
{
    private const double Fs = 3_200_000;
    private const double Spacing = 12_500;
    private const long CenterHz = 146_000_000;

    [Fact]
    public void TheClipBeginsBeforeTheGateOpened()
    {
        var (posted, _) = Run();
        var tx = OnTestChannel(posted);

        // The RF edge marks where the carrier was actually detected. With pre-roll that is no longer
        // the start of the clip, and segmentation downstream depends on the difference.
        var rise = Assert.Single(tx.Markers, m => m.Type == MarkerType.RfEdgeRise);
        output.WriteLine($"  RF edge at {rise.OffsetMs} ms into a {(tx.EndUtc - tx.StartUtc).TotalMilliseconds:F0} ms clip");

        var expected = ChannelBank.PreRollSeconds * 1000;
        Assert.InRange(rise.OffsetMs, expected * 0.5, expected * 1.5);
    }

    [Fact]
    public void TheClipIsLongerThanTheTransmissionByRoughlyThePreRoll()
    {
        var (posted, _) = Run();
        var tx = OnTestChannel(posted);

        // 1.2 s of carrier, plus the pre-roll ahead of it and the squelch hang behind it.
        var durationMs = (tx.EndUtc - tx.StartUtc).TotalMilliseconds;
        output.WriteLine($"  {durationMs:F0} ms clip for a 1200 ms transmission");
        Assert.InRange(durationMs, 1_300, 1_900);
    }

    [Fact]
    public void ThePreRollIsNotCountedAsSignalPresent()
    {
        // Pre-roll is mostly noise from before the carrier appeared. If it counted toward
        // signal-present time, a click could borrow 150 ms of silence and pass the too-short test
        // that exists to catch exactly that.
        var (_, discarded) = RunShortClick();

        // A strong carrier splatters into neighbouring bins, so take the one under test.
        var clip = Assert.Single(discarded, d => d.FrequencyHz == CenterHz + (long)(32 * Spacing));
        Assert.Equal(DiscardReason.TooShort, clip.Reason);
        output.WriteLine($"  a click still reads as {clip.Reason} despite {ChannelBank.PreRollSeconds * 1000:F0} ms of pre-roll");
    }

    /// <summary>The transmission on the bin under test — a strong carrier also splatters into its neighbours.</summary>
    private static TransmissionIngest OnTestChannel(List<TransmissionIngest> posted) =>
        Assert.Single(posted, t => t.FrequencyHz == CenterHz + (long)(32 * Spacing));

    private static (List<TransmissionIngest> Posted, List<DiscardIngest> Discarded) Run(double seconds = 1.2)
    {
        var audioRoot = Path.Combine(Path.GetTempPath(), $"ss-preroll-{Guid.NewGuid():N}");
        Directory.CreateDirectory(audioRoot);
        try
        {
            var channelizer = new PolyphaseChannelizer(Fs, Spacing);
            var posted = new List<TransmissionIngest>();
            var discarded = new List<DiscardIngest>();
            var bank = new ChannelBank(
                channelizer.ChannelCount, channelizer.OutputSampleRate, CenterHz,
                bin => channelizer.BinFrequencyHz(bin, CenterHz),
                openDb: 8, closeDb: 5, hangMs: 300,
                audioRoot, new DateTime(2026, 8, 17, 0, 0, 0, DateTimeKind.Utc),
                f => f, // known frequency: the voice gate is not what is being tested here
                posted.Add,
                NullLogger.Instance,
                postDiscard: discarded.Add);

            var sink = new ForwardingSink(bank);
            channelizer.Process(Noise(0.6), sink);
            channelizer.Process(Tone(seconds), sink);
            channelizer.Process(Noise(0.8), sink);

            return (posted, discarded);
        }
        finally
        {
            Directory.Delete(audioRoot, recursive: true);
        }
    }

    private static (List<TransmissionIngest> Posted, List<DiscardIngest> Discarded) RunShortClick() => Run(0.06);

    /// <summary>A modulated carrier on bin 32 — enough to open the gate and hold it.</summary>
    private static float[] Tone(double seconds)
    {
        var n = (int)(Fs * seconds);
        var iq = new float[n * 2];
        double phase = 0;
        for (var i = 0; i < n; i++)
        {
            var audio = Math.Sin(2 * Math.PI * 800 * i / Fs);
            phase += 2 * Math.PI * ((32 * Spacing) + (2_500 * audio)) / Fs;
            iq[2 * i] = (float)(0.4 * Math.Cos(phase));
            iq[(2 * i) + 1] = (float)(0.4 * Math.Sin(phase));
        }

        return iq;
    }

    private static float[] Noise(double seconds)
    {
        var n = (int)(Fs * seconds);
        var iq = new float[n * 2];
        var rng = new Random(17);
        for (var i = 0; i < iq.Length; i++)
        {
            iq[i] = (float)(((rng.NextDouble() * 2) - 1) * 0.008);
        }

        return iq;
    }

    private sealed class ForwardingSink(ChannelBank bank) : IChannelizerSink
    {
        private long _hop;

        public void OnHop(ReadOnlySpan<float> channels, long hopIndex) => bank.OnHop(channels, _hop++);
    }
}
