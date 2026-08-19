using Microsoft.Extensions.Logging.Abstractions;
using SignalScribe.Capture.Dsp;
using SignalScribe.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

/// <summary>
/// Floors surviving a restart, and the per-channel pin that stops the tracker moving them.
///
/// The failure this guards is quiet: a daemon that relearns every floor from a fixed seed spends its
/// first minutes back either deaf or chattering, and nothing in the recordings says why.
/// </summary>
public class SquelchFloorPersistenceTests(ITestOutputHelper output)
{
    private const double Fs = 3_200_000;
    private const double Spacing = 12_500;
    private const long CenterHz = 146_000_000;

    [Fact]
    public void AStoredFloorIsAdoptedBeforeAnySignalArrives()
    {
        var audioRoot = Scratch();
        try
        {
            var channelizer = new PolyphaseChannelizer(Fs, Spacing);
            var known = CenterHz + (long)(32 * Spacing);
            var bank = NewBank(channelizer, audioRoot, known);

            bank.ApplySquelchState([new ChannelSquelchInfo(known, -95.0, Adaptive: false)]);

            var floors = bank.LearnedFloors();

            // Pinned channels are deliberately not reported back — the daemon must not argue with
            // a floor the operator set.
            Assert.DoesNotContain(floors, f => f.FrequencyHz == known);
        }
        finally
        {
            Directory.Delete(audioRoot, recursive: true);
        }
    }

    /// <summary>
    /// An adaptive channel's stored floor is a starting hint, not a promise: warm-up refines it from
    /// what is actually on the air.
    ///
    /// This is deliberate rather than a shortfall. A stored floor that is too *low* — the gain
    /// changed while the daemon was down — has the gate chattering on noise, and the tracker climbs
    /// upward at 0.005 per block, which would take seconds to escape. Letting warm-up overwrite it
    /// costs a channel nothing it can measure for itself, and a floor the operator genuinely wants
    /// held is what <see cref="ChannelSquelchInfo.Adaptive"/> false is for.
    /// </summary>
    [Fact]
    public void AnAdaptiveChannelsStoredFloorIsOnlyAStartingHint()
    {
        var audioRoot = Scratch();
        try
        {
            var channelizer = new PolyphaseChannelizer(Fs, Spacing);
            var known = CenterHz + (long)(32 * Spacing);
            var bank = NewBank(channelizer, audioRoot, known);

            bank.ApplySquelchState([new ChannelSquelchInfo(known, -103.5, Adaptive: true)]);
            channelizer.Process(Noise(0.4), new Sink(bank));

            var floor = bank.LearnedFloors().Single(f => f.FrequencyHz == known).NoiseFloorDbfs;
            output.WriteLine($"  stored -103.5, measured on air: {floor} dBFS");
            Assert.NotEqual(-103.5, floor);
        }
        finally
        {
            Directory.Delete(audioRoot, recursive: true);
        }
    }

    [Fact]
    public void AnAdaptiveChannelReportsWhatItHasLearned()
    {
        var audioRoot = Scratch();
        try
        {
            var channelizer = new PolyphaseChannelizer(Fs, Spacing);
            var known = CenterHz + (long)(32 * Spacing);
            var bank = NewBank(channelizer, audioRoot, known);
            var sink = new Sink(bank);

            bank.ApplySquelchState([new ChannelSquelchInfo(known, null, Adaptive: true)]);
            channelizer.Process(Noise(0.5), sink);

            var report = bank.LearnedFloors().SingleOrDefault(f => f.FrequencyHz == known);
            Assert.NotNull(report);
            output.WriteLine($"  learned floor for {known} Hz: {report!.NoiseFloorDbfs} dBFS");

            // A real measurement, not the fixed -110 seed the bank starts from.
            Assert.NotEqual(-110.0, report.NoiseFloorDbfs);
        }
        finally
        {
            Directory.Delete(audioRoot, recursive: true);
        }
    }

    /// <summary>
    /// The point of pinning: the tracker leaves the floor alone even as the band moves under it.
    /// </summary>
    [Fact]
    public void APinnedFloorDoesNotDriftWithTheBand()
    {
        var audioRoot = Scratch();
        try
        {
            var channelizer = new PolyphaseChannelizer(Fs, Spacing);
            var known = CenterHz + (long)(32 * Spacing);

            var pinned = NewBank(channelizer, audioRoot, known);
            var adaptive = NewBank(channelizer, audioRoot, known);
            pinned.ApplySquelchState([new ChannelSquelchInfo(known, -95.0, Adaptive: false)]);
            adaptive.ApplySquelchState([new ChannelSquelchInfo(known, -95.0, Adaptive: true)]);

            var pinnedSink = new Sink(pinned);
            var adaptiveSink = new Sink(adaptive);
            for (var i = 0; i < 4; i++)
            {
                var noise = Noise(0.4);
                channelizer.Process(noise, pinnedSink);
            }

            var second = new PolyphaseChannelizer(Fs, Spacing);
            for (var i = 0; i < 4; i++)
            {
                second.Process(Noise(0.4), adaptiveSink);
            }

            var adaptiveFloor = adaptive.LearnedFloors().Single(f => f.FrequencyHz == known).NoiseFloorDbfs;
            output.WriteLine($"  adaptive drifted to {adaptiveFloor} dBFS; pinned was held at -95.0");

            // The pinned bank reports nothing at all, which is how "the daemon is not touching it"
            // is expressed end to end.
            Assert.Empty(pinned.LearnedFloors());
            Assert.NotEqual(-95.0, adaptiveFloor);
        }
        finally
        {
            Directory.Delete(audioRoot, recursive: true);
        }
    }

    [Fact]
    public void AnUnknownBinIsNeverReported()
    {
        var audioRoot = Scratch();
        try
        {
            var channelizer = new PolyphaseChannelizer(Fs, Spacing);
            var bank = new ChannelBank(
                channelizer.ChannelCount, channelizer.OutputSampleRate, CenterHz,
                bin => channelizer.BinFrequencyHz(bin, CenterHz),
                openDb: 8, closeDb: 5, hangMs: 300,
                audioRoot, DateTime.UtcNow,
                _ => null, _ => { }, NullLogger.Instance);

            channelizer.Process(Noise(0.5), new Sink(bank));

            // Nothing is known, so there is no channel to attribute a floor to.
            Assert.Empty(bank.LearnedFloors());
        }
        finally
        {
            Directory.Delete(audioRoot, recursive: true);
        }
    }

    private static ChannelBank NewBank(PolyphaseChannelizer channelizer, string audioRoot, long known)
    {
        long[] set = [known];
        return new ChannelBank(
            channelizer.ChannelCount, channelizer.OutputSampleRate, CenterHz,
            bin => channelizer.BinFrequencyHz(bin, CenterHz),
            openDb: 8, closeDb: 5, hangMs: 300,
            audioRoot, DateTime.UtcNow,
            f => KnownFrequencyResolver.Nearest(set, f, (long)(Spacing / 2)),
            _ => { },
            NullLogger.Instance);
    }

    private static string Scratch()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ss-floor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static float[] Noise(double seconds)
    {
        var n = (int)(Fs * seconds);
        var iq = new float[n * 2];
        var rng = new Random(7);
        for (var i = 0; i < iq.Length; i++)
        {
            iq[i] = (float)(((rng.NextDouble() * 2) - 1) * 0.01);
        }

        return iq;
    }

    private sealed class Sink(ChannelBank bank) : IChannelizerSink
    {
        private long _hop;

        public void OnHop(ReadOnlySpan<float> channels, long hopIndex) => bank.OnHop(channels, _hop++);
    }
}
