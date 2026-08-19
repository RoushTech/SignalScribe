using Concentus;
using Concentus.Oggfile;
using SignalScribe.Capture.Dsp;
using SignalScribe.Modem;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

/// <summary>
/// "What is this signal?" — offline characterisation of a recorded clip, for the recurring case
/// where a frequency carries something none of the decoders claim. Prints burst structure, spectrum
/// peaks, an estimated symbol rate, and an AFSK decode attempt over the clip audio.
///
/// Point <c>SIGNALSCRIBE_CLIP</c> at one .ogg, or <c>SIGNALSCRIBE_CLIP_GLOB</c> at a
/// directory/prefix (e.g. <c>data/audio/2026-08-17/145287500</c>) to analyse the newest few.
/// Without either it is silent and green, so CI never notices it.
///
/// Read the numbers knowing what the clip has been through: de-emphasis (−6 dB/octave above
/// 212 Hz), a 300 Hz high-pass, a soft limiter, and Opus at 32 kbps. A discriminator-domain mode
/// (POCSAG's ±4.5 kHz shift, C4FM's levels) is heavily coloured here — tone positions survive,
/// levels do not.
/// </summary>
public class ClipSpectrumDiag(ITestOutputHelper output)
{
    private const int Rate = 16_000;

    [Fact]
    public void DescribeClip()
    {
        var paths = ResolvePaths();
        if (paths.Count == 0)
        {
            output.WriteLine("SIGNALSCRIBE_CLIP / SIGNALSCRIBE_CLIP_GLOB not set — skipping.");
            return;
        }

        foreach (var path in paths)
        {
            var pcm = DecodeOpus(path);
            output.WriteLine($"\n=== {Path.GetFileName(path)} — {pcm.Length / (double)Rate:F2}s ===");
            Bursts(pcm);
            Spectrum(pcm);
            SymbolRate(pcm);
            TryAfsk(pcm);
            SymbolProbe(pcm);
        }
    }

    /// <summary>
    /// Recovers 4800-baud symbols from the clip by inverting the de-emphasis, then prints their
    /// level histogram — two humps is GMSK (D-STAR), four is C4FM (YSF/P25/NXDN/DMR) — and runs the
    /// real framers over them. The 300 Hz high-pass is not inverted: what it removed is DC wander,
    /// which the framers track out anyway.
    /// </summary>
    private void SymbolProbe(float[] pcm)
    {
        // Inverse of s += alpha * (x - s): x = prev + (s - prev) / alpha.
        var alpha = 1f - MathF.Exp(-1f / (Rate * 750e-6f));
        var restored = new float[pcm.Length];
        float prev = 0;
        for (var i = 0; i < pcm.Length; i++)
        {
            restored[i] = prev + ((pcm[i] - prev) / alpha);
            prev = pcm[i];
        }

        var sync = new SymbolSynchronizer(Rate, 4_800);
        var dstar = new SignalScribe.Capture.Digital.DStar.DStarFramer();
        var c4fm = new SignalScribe.Capture.Digital.C4fm.C4fmSyncDetector();
        var symbols = new List<double>();
        foreach (var s in restored)
        {
            if (sync.Feed(s, out var recovered))
            {
                symbols.Add(recovered);
                dstar.Feed(recovered);
                c4fm.Feed(recovered);
            }
        }

        if (symbols.Count < 100)
        {
            output.WriteLine("symbol probe: too few symbols recovered");
            return;
        }

        // Robust scale, then a 33-bin histogram over ±2× that scale.
        var scale = symbols.Select(Math.Abs).OrderBy(v => v).ElementAt((int)(symbols.Count * 0.75));
        var hist = new int[33];
        foreach (var s in symbols)
        {
            var bin = (int)Math.Round((Math.Clamp(s / (scale * 2), -1, 1) + 1) * 16);
            hist[bin]++;
        }

        var top = hist.Max();
        output.WriteLine("symbol histogram (−2×scale … +2×scale):");
        output.WriteLine("  " + string.Concat(hist.Select(h => " .:-=+*#@"[(int)(h * 8L / Math.Max(1, top))])));
        output.WriteLine(
            $"framers over reconstructed symbols: {dstar.HeaderCount} D-STAR header(s), {dstar.SyncCount} D-STAR sync(s), "
            + $"{c4fm.SyncCount(SignalScribe.Enums.DetectedMode.Dmr)} DMR / "
            + $"{c4fm.SyncCount(SignalScribe.Enums.DetectedMode.P25Phase1)} P25 / "
            + $"{c4fm.SyncCount(SignalScribe.Enums.DetectedMode.Ysf)} YSF sync(s)");
    }

    /// <summary>10 ms RMS envelope, printed as a coarse on/off map with burst statistics.</summary>
    private void Bursts(float[] pcm)
    {
        var block = Rate / 100;
        var rms = new List<double>();
        for (var i = 0; i + block <= pcm.Length; i += block)
        {
            double sum = 0;
            for (var j = i; j < i + block; j++)
            {
                sum += pcm[j] * pcm[j];
            }

            rms.Add(Math.Sqrt(sum / block));
        }

        var peak = rms.Max();
        if (peak <= 0)
        {
            output.WriteLine("burst map: silent clip");
            return;
        }

        var map = string.Concat(rms.Select(v => v > peak * 0.25 ? '#' : '.'));
        output.WriteLine($"burst map (10 ms cells, # = above −12 dB of peak):");
        for (var i = 0; i < map.Length; i += 100)
        {
            output.WriteLine("  " + map.Substring(i, Math.Min(100, map.Length - i)));
        }

        // Burst cadence: lengths of consecutive runs.
        var runs = new List<(char What, int Ms)>();
        foreach (var cell in map)
        {
            if (runs.Count > 0 && runs[^1].What == cell)
            {
                runs[^1] = (cell, runs[^1].Ms + 10);
            }
            else
            {
                runs.Add((cell, 10));
            }
        }

        var on = runs.Where(r => r.What == '#').Select(r => r.Ms).ToList();
        var off = runs.Where(r => r.What == '.').Select(r => r.Ms).ToList();
        if (on.Count > 1)
        {
            output.WriteLine($"bursts: {on.Count} on-runs, median {Median(on)} ms on / {(off.Count > 0 ? Median(off) : 0)} ms off");
        }
    }

    /// <summary>Averaged 4096-point spectrum; the top peaks say which tones live where.</summary>
    private void Spectrum(float[] pcm)
    {
        const int N = 4_096;
        var fft = new Fft(N);
        var frame = new float[N * 2];
        var power = new double[N / 2];
        var windows = 0;
        for (var start = 0; start + N <= pcm.Length; start += N / 2)
        {
            for (var i = 0; i < N; i++)
            {
                var w = 0.5f * (1 - MathF.Cos(2 * MathF.PI * i / N));
                frame[2 * i] = pcm[start + i] * w;
                frame[(2 * i) + 1] = 0;
            }

            fft.Transform(frame);
            for (var b = 0; b < N / 2; b++)
            {
                power[b] += (frame[2 * b] * frame[2 * b]) + (frame[(2 * b) + 1] * frame[(2 * b) + 1]);
            }

            windows++;
        }

        if (windows == 0)
        {
            return;
        }

        var binHz = (double)Rate / N;
        var peaks = Enumerable.Range(2, (N / 2) - 3)
            .Where(b => power[b] > power[b - 1] && power[b] >= power[b + 1])
            .OrderByDescending(b => power[b])
            .Take(8)
            .OrderBy(b => b)
            .Select(b => $"{b * binHz:F0} Hz ({10 * Math.Log10(power[b] / power.Max()):F0} dB)");
        output.WriteLine("spectrum peaks: " + string.Join(", ", peaks));
    }

    /// <summary>
    /// Symbol-rate estimate from the spectrum of the rectified derivative — transitions happen at
    /// the baud rate regardless of the modulation's tone plan.
    /// </summary>
    private void SymbolRate(float[] pcm)
    {
        const int N = 8_192;
        if (pcm.Length < N + 1)
        {
            return;
        }

        var fft = new Fft(N);
        var frame = new float[N * 2];
        var power = new double[N / 2];
        for (var start = 0; start + N + 1 <= pcm.Length; start += N)
        {
            for (var i = 0; i < N; i++)
            {
                frame[2 * i] = Math.Abs(pcm[start + i + 1] - pcm[start + i]);
                frame[(2 * i) + 1] = 0;
            }

            fft.Transform(frame);
            for (var b = 0; b < N / 2; b++)
            {
                power[b] += (frame[2 * b] * frame[2 * b]) + (frame[(2 * b) + 1] * frame[(2 * b) + 1]);
            }
        }

        var binHz = (double)Rate / N;
        var floor = 50; // ignore sub-100 Hz — envelope, not clock
        // Top several, not just the winner: rectification generates harmonics, so a 2400-baud
        // signal shows lines at both 2400 and 4800 and the strongest alone can mislead.
        var top = Enumerable.Range(floor + 1, (N / 2) - floor - 2)
            .Where(b => power[b] > power[b - 1] && power[b] >= power[b + 1])
            .OrderByDescending(b => power[b])
            .Take(5)
            .Select(b => $"{b * binHz:F0} Hz ({10 * Math.Log10(power[b] / power.Skip(floor).Max()):F0} dB)");
        output.WriteLine("clock lines (rectified-derivative spectrum): " + string.Join(", ", top));
    }

    /// <summary>The soft TNC over the clip audio. Colour and offset degrade it, but a decode is proof.</summary>
    private void TryAfsk(float[] pcm)
    {
        var receiver = PacketReceiver.CreateStandard(Rate);
        var packets = new List<DecodedPacket>();
        receiver.PacketReceived += packets.Add;
        receiver.ProcessSamples(pcm);
        output.WriteLine(packets.Count > 0
            ? "AFSK: " + string.Join(" | ", packets.Select(p => p.Tnc2))
            : "AFSK: no decode");
    }

    private static List<string> ResolvePaths()
    {
        var single = Environment.GetEnvironmentVariable("SIGNALSCRIBE_CLIP");
        if (single is not null && File.Exists(single))
        {
            return [single];
        }

        var glob = Environment.GetEnvironmentVariable("SIGNALSCRIBE_CLIP_GLOB");
        if (glob is null)
        {
            return [];
        }

        var dir = Path.GetDirectoryName(glob);
        if (dir is null || !Directory.Exists(dir))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(dir, Path.GetFileName(glob) + "*")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(4)];
    }

    private static int Median(List<int> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }

    private static float[] DecodeOpus(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = OpusCodecFactory.CreateDecoder(Rate, 1);
        var ogg = new OpusOggReadStream(decoder, stream);
        var samples = new List<float>(Rate * 10);
        while (ogg.HasNextPacket)
        {
            var packet = ogg.DecodeNextPacket();
            if (packet is not null)
            {
                foreach (var s in packet)
                {
                    samples.Add(s / 32768f);
                }
            }
        }

        return [.. samples];
    }
}
