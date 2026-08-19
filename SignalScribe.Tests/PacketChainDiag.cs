using SignalScribe.Capture.Dsp;
using SignalScribe.Modem;
using SignalScribe.Modem.Ax25;
using SignalScribe.Modem.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

/// <summary>
/// Diagnostic: what survives of a Bell 202 packet at each stage of the capture chain. Prints, does
/// not assert.
/// </summary>
public class PacketChainDiag(ITestOutputHelper output)
{
    private const double Fs = 3_200_000;
    private const double Spacing = 12_500;
    private const int AudioRate = 16_000;

    [Fact]
    public void OffsetSweep()
    {
        var frame = Ax25Encoder.EncodeUiFrame("KD9ABC-7", "!4221.55N/08750.12W#sweep", "WIDE1-1");
        var tones = new AfskModulator(AudioRate).GenerateFrame(frame, leadFlags: 48, tailFlags: 8);

        foreach (var deviation in new[] { 2_500.0, 3_000.0 })
        {
            foreach (var offset in new[] { 0.0, 1_000, 1_500, 2_000, 2_500, 3_000, -2_500, -3_000 })
            {
                var channelizer = new PolyphaseChannelizer(Fs, Spacing);
                var sink = new PeakBinSink(channelizer.ChannelCount);
                channelizer.Process(FmModulate(tones, AudioRate, (32 * Spacing) + offset, deviation), sink);

                var demod = new NbfmDemodulator(channelizer.OutputSampleRate);
                var pcm = new float[tones.Length * 2];
                var written = demod.Process(sink.Samples(sink.PeakBin()), pcm);

                var ok = Decode(pcm[..written], AudioRate) != "NOTHING";

                // Same signal, straight off the discriminator: separates a channelizer loss from an
                // audio-path loss.
                var raw = Discriminate(sink.Samples(sink.PeakBin()), channelizer.OutputSampleRate);
                var rawOk = Decode(raw, (int)channelizer.OutputSampleRate) != "NOTHING";

                // Same audio path, but with the deviation reference raised so the signal stays well
                // below the soft limiter's knee. If this decodes where the normal path does not, the
                // limiter is what is eating the packet.
                var roomy = new NbfmDemodulator(channelizer.OutputSampleRate, deviationHz: 20_000);
                var roomyPcm = new float[tones.Length * 2];
                var roomyWritten = roomy.Process(sink.Samples(sink.PeakBin()), roomyPcm);
                var roomyOk = Decode(roomyPcm[..roomyWritten], AudioRate) != "NOTHING";

                output.WriteLine(
                    $"  dev {deviation,5:F0} offset {offset,6:F0} -> audio {(ok ? "yes" : "no "),-3} " +
                    $" raw {(rawOk ? "yes" : "no "),-3}  below-knee {(roomyOk ? "yes" : "no")}");
            }
        }
    }

    [Fact]
    public void WhereDoesThePacketDie()
    {
        var frame = Ax25Encoder.EncodeUiFrame("KD9ABC-7", "!4221.55N/08750.12W#diag", "WIDE1-1");
        var tones = new AfskModulator(AudioRate).GenerateFrame(frame, leadFlags: 48, tailFlags: 8);
        Report("modulator output", tones);

        var iq = FmModulate(tones, AudioRate, offsetHz: 32 * Spacing, deviationHz: 3_000);
        var channelizer = new PolyphaseChannelizer(Fs, Spacing);
        var sink = new PeakBinSink(channelizer.ChannelCount);
        channelizer.Process(iq, sink);

        // Straight discriminator, no de-emphasis / limiter / high-pass, at the channel rate.
        var bin = sink.Samples(sink.PeakBin());
        var raw = Discriminate(bin, channelizer.OutputSampleRate);
        Report($"raw discriminator @{channelizer.OutputSampleRate:F0}", raw, channelizer.OutputSampleRate);
        output.WriteLine($"  decoded from raw discriminator: {Decode(raw, (int)channelizer.OutputSampleRate)}");

        var demod = new NbfmDemodulator(channelizer.OutputSampleRate);
        var pcm = new float[tones.Length * 2];
        var written = demod.Process(bin, pcm);
        var audio = pcm[..written];
        Report("full audio path @16000", audio);
        output.WriteLine($"  decoded from audio path: {Decode(audio, AudioRate)}");
    }

    private string Decode(float[] samples, int rate)
    {
        var receiver = PacketReceiver.CreateStandard(rate);
        var packets = new List<DecodedPacket>();
        receiver.PacketReceived += packets.Add;
        receiver.ProcessSamples(samples);
        return packets.Count == 0 ? "NOTHING" : packets[0].Tnc2;
    }

    private void Report(string stage, float[] samples, double rate = AudioRate)
    {
        double sum = 0, peak = 0;
        foreach (var s in samples)
        {
            sum += s * (double)s;
            peak = Math.Max(peak, Math.Abs(s));
        }

        var mark = Goertzel(samples, 1_200, rate);
        var space = Goertzel(samples, 2_200, rate);
        output.WriteLine(
            $"{stage,-32} n={samples.Length,7} rms={Math.Sqrt(sum / samples.Length):F4} peak={peak:F4} " +
            $"mark/space={mark / Math.Max(1e-12, space):F2}");
    }

    private static double Goertzel(float[] samples, double freq, double rate)
    {
        var coeff = 2 * Math.Cos(2 * Math.PI * freq / rate);
        double s1 = 0, s2 = 0, total = 0;
        var block = 0;
        foreach (var x in samples)
        {
            var s0 = x + (coeff * s1) - s2;
            s2 = s1;
            s1 = s0;
            if (++block < 512)
            {
                continue;
            }

            total += (s1 * s1) + (s2 * s2) - (coeff * s1 * s2);
            s1 = s2 = 0;
            block = 0;
        }

        return total;
    }

    private static float[] Discriminate(float[] iq, double rate)
    {
        var outBuf = new float[iq.Length / 2];
        float prevI = iq[0], prevQ = iq[1];
        var scale = rate / (2 * Math.PI);
        var n = 0;
        for (var s = 2; s + 1 < iq.Length; s += 2)
        {
            var i = iq[s];
            var q = iq[s + 1];
            outBuf[n++] = (float)(Math.Atan2((prevI * q) - (prevQ * i), (prevI * i) + (prevQ * q)) * scale / 3_000);
            prevI = i;
            prevQ = q;
        }

        return outBuf;
    }

    private static float[] FmModulate(float[] audio, int audioRate, double offsetHz, double deviationHz)
    {
        var n = (int)(Fs * (audio.Length / (double)audioRate));
        var iq = new float[n * 2];
        var step = audioRate / Fs;
        double phase = 0, position = 0;
        for (var i = 0; i < n; i++)
        {
            var index = (int)position;
            var frac = (float)(position - index);
            var sample = index + 1 < audio.Length ? audio[index] + ((audio[index + 1] - audio[index]) * frac) : audio[^1];
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
