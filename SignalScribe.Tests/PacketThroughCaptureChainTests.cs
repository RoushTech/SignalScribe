using Microsoft.Extensions.Logging.Abstractions;
using SignalScribe.Capture.Dsp;
using SignalScribe.Contracts;
using SignalScribe.Enums;
using SignalScribe.Modem;
using SignalScribe.Modem.Ax25;
using SignalScribe.Modem.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

/// <summary>
/// A packet through the real capture chain — channelizer, discriminator, de-emphasis, soft limiter,
/// 300 Hz high-pass, resample to 16 kHz — rather than straight into the modem.
///
/// This is the test that decides where the soft TNC taps. SignalScribe's audio path is built for
/// speech: 750 µs de-emphasis rolls off above 212 Hz, which attenuates Bell 202's 2200 Hz space tone
/// by several dB relative to its 1200 Hz mark. A demodulator that compared the two tones directly
/// would be badly skewed by that. This one runs an AGC per tone, which is exactly the compensation
/// needed — but "should work" is not "does work", and 144.390 is worth being sure about.
/// </summary>
public class PacketThroughCaptureChainTests(ITestOutputHelper output)
{
    private const double Fs = 3_200_000;
    private const double Spacing = 12_500;
    private const int AudioRate = 16_000;

    private static byte[] Beacon(string source = "KD9ABC-7")
        => Ax25Encoder.EncodeUiFrame(source, "!4221.55N/08750.12W#PHG5130 through the chain", "WIDE1-1,WIDE2-2");

    [Theory]
    [InlineData(0)]         // carrier on the bin centre
    [InlineData(2_500)]     // 144.390 sits 2.5 kHz off the 12.5 kHz analysis grid — where APRS lives
    [InlineData(-2_500)]
    [InlineData(-1_750)]    // the digipeater measured 1.75 kHz low on air
    [InlineData(5_000)]     // the worst case the grid allows
    public void DecodesAcrossTheWholeOffGridRange(double offsetHz)
    {
        var packets = RunThroughChain(Beacon(), offsetHz, deviationHz: 3_000);

        var packet = Assert.Single(packets);
        Assert.Equal("KD9ABC", packet.Packet.Frame.Source.Callsign);
        output.WriteLine($"  offset {offsetHz,6:F0} Hz -> {packet.Packet.Tnc2}");
    }

    [Theory]
    [InlineData(2_000)]
    [InlineData(3_000)]
    [InlineData(3_500)]     // an over-deviating station
    public void DecodesAcrossTheDeviationRangeStationsActuallyUse(double deviationHz)
    {
        Assert.Single(RunThroughChain(Beacon(), offsetHz: 2_500, deviationHz));
    }

    [Fact]
    public void AprsOnAnUnknownFrequencyEarnsALabelledChannelWithItsPacketsAttached()
    {
        // The whole point of stage 2, end to end through the real gate. 144.390 previously produced
        // a discard marked "not speech"; it should now post, name itself APRS, and carry the frame.
        var audioRoot = Path.Combine(Path.GetTempPath(), $"ss-aprs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(audioRoot);
        try
        {
            var channelizer = new PolyphaseChannelizer(Fs, Spacing);
            var posted = new List<TransmissionIngest>();
            var discarded = new List<DiscardIngest>();
            var bank = new ChannelBank(
                channelizer.ChannelCount, channelizer.OutputSampleRate, 146_000_000,
                bin => channelizer.BinFrequencyHz(bin, 146_000_000),
                openDb: 8, closeDb: 5, hangMs: 300,
                audioRoot, new DateTime(2026, 8, 16, 0, 0, 0, DateTimeKind.Utc),
                _ => null, // unknown frequency — the gate decides, and packet decoding is on
                posted.Add,
                NullLogger.Instance,
                postDiscard: discarded.Add);

            var tones = new AfskModulator(AudioRate).GenerateFrame(Beacon(), leadFlags: 64, tailFlags: 8);
            var sink = new ForwardingSink(bank);
            channelizer.Process(Silence(0.4), sink);
            channelizer.Process(FmModulate(tones, AudioRate, (32 * Spacing) + 2_500, 3_000), sink);
            channelizer.Process(Silence(0.8), sink);

            Assert.Empty(discarded);
            var tx = Assert.Single(posted);
            Assert.Equal(DetectedMode.Afsk1200, tx.Mode);

            var packet = Assert.Single(tx.Packets!);
            Assert.StartsWith("KD9ABC-7>APRS,WIDE1-1,WIDE2-2:", packet.Tnc2);
            Assert.Equal("KD9ABC-7", packet.Source);
            Assert.InRange(packet.OffsetMs, 0, (int)(tx.EndUtc - tx.StartUtc).TotalMilliseconds);
            output.WriteLine($"  {tx.FrequencyHz} Hz {tx.Mode}: {packet.Tnc2}");
        }
        finally
        {
            Directory.Delete(audioRoot, recursive: true);
        }
    }

    private static float[] Silence(double seconds)
    {
        var n = (int)(Fs * seconds);
        var iq = new float[n * 2];
        var rng = new Random(3);
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

    /// <summary>
    /// Modulates an AX.25 frame as Bell 202 AFSK onto an FM carrier, runs it through the channelizer
    /// and demodulator, and reads the packets the demodulator decoded off its own discriminator.
    /// </summary>
    private static List<TimedPacket> RunThroughChain(byte[] frame, double offsetHz, double deviationHz)
    {
        // The modem's own modulator renders the audio; FM-modulating it is what a packet radio does.
        var tones = new AfskModulator(AudioRate).GenerateFrame(frame, leadFlags: 48, tailFlags: 8);

        // Park the carrier on bin 32 and take the offset from there. Baseband zero is the DC bin,
        // which is not a channel the bank ever gates and would only measure the filterbank's own edge.
        var iq = FmModulate(tones, AudioRate, (32 * Spacing) + offsetHz, deviationHz);

        var channelizer = new PolyphaseChannelizer(Fs, Spacing);
        var sink = new PeakBinSink(channelizer.ChannelCount);
        channelizer.Process(iq, sink);

        // The demodulator taps the soft TNC off its own discriminator, so decoding is a side effect
        // of producing audio — exactly as it happens in the capture bank.
        var demod = new NbfmDemodulator(channelizer.OutputSampleRate, decodePackets: true);
        var pcm = new float[(int)(AudioRate * (tones.Length / (double)AudioRate) * 1.2) + 4096];
        demod.Process(sink.Samples(sink.PeakBin()), pcm);

        return [.. demod.Packets];
    }

    /// <summary>FM-modulates an audio waveform onto a carrier at <paramref name="offsetHz"/> from the bin centre.</summary>
    private static float[] FmModulate(float[] audio, int audioRate, double offsetHz, double deviationHz)
    {
        var seconds = audio.Length / (double)audioRate;
        var n = (int)(Fs * seconds);
        var iq = new float[n * 2];
        var step = audioRate / Fs;

        double phase = 0, position = 0;
        for (var i = 0; i < n; i++)
        {
            var index = (int)position;
            var frac = (float)(position - index);
            var sample = index + 1 < audio.Length
                ? audio[index] + ((audio[index + 1] - audio[index]) * frac)
                : audio[^1];
            position += step;

            phase += 2 * Math.PI * (offsetHz + (deviationHz * sample)) / Fs;
            iq[2 * i] = (float)(0.4 * Math.Cos(phase));
            iq[(2 * i) + 1] = (float)(0.4 * Math.Sin(phase));
        }

        return iq;
    }

    private sealed class PeakBinSink(int channels) : IChannelizerSink
    {
        private readonly List<float>[] _buf = [.. Enumerable.Range(0, channels).Select(_ => new List<float>())];
        private readonly double[] _power = new double[channels];

        public void OnHop(ReadOnlySpan<float> frame, long hopIndex)
        {
            for (var c = 0; c < _power.Length; c++)
            {
                _buf[c].Add(frame[2 * c]);
                _buf[c].Add(frame[(2 * c) + 1]);
                _power[c] += (frame[2 * c] * frame[2 * c]) + (frame[(2 * c) + 1] * frame[(2 * c) + 1]);
            }
        }

        public int PeakBin()
        {
            var best = 1;
            for (var c = 1; c < _power.Length; c++)
            {
                if (_power[c] > _power[best])
                {
                    best = c;
                }
            }

            return best;
        }

        public float[] Samples(int bin) => [.. _buf[bin]];
    }
}
