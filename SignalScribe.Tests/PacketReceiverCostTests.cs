using System.Diagnostics;
using SignalScribe.Modem;
using SignalScribe.Modem.Ax25;
using SignalScribe.Modem.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

public class PacketReceiverCostTests(ITestOutputHelper output)
{
    private const int Rate = 25_000;

    /// <summary>
    /// Whether the soft TNC can run on every open gate, or only where a channel is already known to
    /// carry packet, comes down to what it costs. It is far heavier than the tone and mode detectors
    /// — two profiles, each a bandpass FIR, two quadrature correlator pairs, two AGCs, a lowpass FIR
    /// and a PLL, per sample — so this is the number that decides the design.
    ///
    /// It taps the demodulator, so it runs once per *open gate*, not once per channel in the bank.
    /// </summary>
    [Fact]
    public void CostPerDecodingGateStaysInsideTheBudget()
    {
        const int Seconds = 30;

        // Real packet audio, not noise: the deframer's work depends on finding flags and bits.
        var frame = Ax25Encoder.EncodeUiFrame("KD9ABC-7", "!4221.55N/08750.12W#cost", "WIDE1-1,WIDE2-2");
        var one = new AfskModulator(Rate).GenerateFrame(frame, leadFlags: 32, tailFlags: 4);
        var samples = new float[Rate * Seconds];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = one[i % one.Length];
        }

        var warm = PacketReceiver.CreateStandard(Rate);
        warm.ProcessSamples(samples.AsSpan(0, Rate));

        // Best of several passes. What is being measured is how much work the decoder does, and the
        // rest of the suite running alongside it only ever makes a pass look slower — measured at
        // 3.5% on an idle machine and 6.4% under contention, which is enough to make a single-shot
        // threshold flake. The fastest pass is the one least polluted by the neighbours.
        var timed = PacketReceiver.CreateStandard(Rate);
        var best = double.MaxValue;
        for (var pass = 0; pass < 3; pass++)
        {
            var sw = Stopwatch.StartNew();
            timed.ProcessSamples(samples);
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalSeconds);
        }

        var fractionOfOneCore = best / Seconds;
        output.WriteLine($"  {best * 1000:F0} ms for {Seconds}s of signal (best of 3, {timed.PacketCount} packets)");
        output.WriteLine($"  {fractionOfOneCore * 100:F3}% of one core per decoding gate");
        output.WriteLine($"  {fractionOfOneCore * 100 * 8:F2}% at the 8-decoder budget");
        output.WriteLine($"  {fractionOfOneCore * 100 * 32:F2}% if it ran on all 32 gates (it does not)");

        // This measurement shaped the design. Idle, the decoder costs ~3.5-4.7% of a core per gate:
        // forty times the tone detector, so it cannot run everywhere the way CTCSS does. On all 32
        // gates that is about one and a half cores on top of the channelizer's 70%, and capture must
        // never fall behind the sample stream. Hence ChannelBank.MaxPacketDecoders, and hence
        // spending those decoders only where a packet is plausible.
        //
        // The assertion is deliberately loose. xunit runs test classes in parallel, so this competes
        // with every other DSP test in the suite and the same code measures 3.5% idle and 8.3% under
        // that contention — a threshold tight enough to certify the budget would simply flake. What
        // this catches is a change that makes the decoder an order of magnitude more expensive; the
        // printed figures are what to read when tuning the budget, and they should be taken from an
        // otherwise-idle machine.
        Assert.True(
            fractionOfOneCore < 0.15,
            $"cost {fractionOfOneCore:P1} of one core per gate — an order-of-magnitude regression, revisit the decoder");
    }
}
