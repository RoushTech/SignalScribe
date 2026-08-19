using SignalScribe.Capture.Dsp;
using SignalScribe.Enums;
using Xunit;

namespace SignalScribe.Tests;

/// <summary>
/// The 144.980 regression: a DMR hotspot 5 kHz off its filterbank bin was reported as analog FM.
///
/// Two failures stacked. The demodulator's DC tracker chased the squelch-closed TDMA gaps, where the
/// discriminator is noise averaging to zero, so the carrier offset it subtracted was a ±700 Hz
/// sawtooth rather than a constant — smearing DMR's four levels into a hump the classifier could
/// only call analog. And nothing could *name* DMR anyway, because naming a four-level mode takes its
/// sync pattern (CLAUDE.md) and no DMR framer existed. These tests pin both fixes at the
/// demodulator level, driving IQ the way the channel bank does: block by block, with SignalPresent
/// following the squelch.
/// </summary>
public class DmrThroughDemodulatorTests
{
    private const double ChannelRate = 25_000;

    private const int BlockSamples = 256; // the bank's gate-decision granularity, ~10.24 ms

    /// <summary>BS sourced voice sync (ETSI TS 102 361-1 table 9.2) as its 24 symbol levels.</summary>
    private static readonly double[] SyncSymbols = Expand(0x755FD7DF75F7);

    private static double[] Expand(ulong word)
    {
        var symbols = new double[24];
        for (var d = 0; d < 24; d++)
        {
            var dibit = (int)((word >> (2 * (23 - d))) & 0b11);
            symbols[d] = dibit switch
            {
                0b01 => DigitalSignals.DmrOuterHz,
                0b00 => DigitalSignals.DmrInnerHz,
                0b10 => -DigitalSignals.DmrInnerHz,
                _ => -DigitalSignals.DmrOuterHz,
            };
        }

        return symbols;
    }

    /// <summary>FM-modulates a deviation waveform onto an offset carrier at the channel rate.</summary>
    private static float[] Modulate(float[] deviationHz, double offsetHz, Func<int, bool>? carrierOn = null)
    {
        var iq = new float[deviationHz.Length * 2];
        var noise = new Random(5);
        double phase = 0;
        for (var i = 0; i < deviationHz.Length; i++)
        {
            phase += 2 * Math.PI * (offsetHz + deviationHz[i]) / ChannelRate;
            var amplitude = carrierOn?.Invoke(i) ?? true ? 0.4 : 0.0;
            iq[2 * i] = (float)((amplitude * Math.Cos(phase)) + (((noise.NextDouble() * 2) - 1) * 0.008));
            iq[(2 * i) + 1] = (float)((amplitude * Math.Sin(phase)) + (((noise.NextDouble() * 2) - 1) * 0.008));
        }

        return iq;
    }

    /// <summary>
    /// Feeds IQ to a demodulator the way ChannelBank does: in gate blocks, with SignalPresent set
    /// from whether the carrier is actually up in that block.
    /// </summary>
    private static NbfmDemodulator Demodulate(float[] iq, Func<int, bool> presentAt)
    {
        var demod = new NbfmDemodulator(ChannelRate, decodeDigital: true);
        var pcm = new float[BlockSamples];
        for (var start = 0; start + BlockSamples <= iq.Length / 2; start += BlockSamples)
        {
            demod.SignalPresent = presentAt(start);
            demod.Process(iq.AsSpan(2 * start, 2 * BlockSamples), pcm);
        }

        return demod;
    }

    /// <summary>27.5 ms bursts in 60 ms frames — a subscriber radio's TDMA duty cycle.</summary>
    private static bool InBurst(int sample)
    {
        var ms = sample * 1000.0 / ChannelRate;
        return ms % 60.0 < 27.5;
    }

    /// <summary>
    /// TDMA C4FM with no sync embedded, 5 kHz off bin centre — the histogram path alone. Before the
    /// DC freeze this classified as analog FM; the four levels only survive if the carrier estimate
    /// holds still through the gaps.
    /// </summary>
    [Fact]
    public void BurstyFourLevelOffBinReadsAsDigital()
    {
        var deviation = DigitalSignals.C4fm(ChannelRate, 3.0, DigitalSignals.DmrOuterHz, DigitalSignals.DmrInnerHz, DigitalSignals.C4fmBaud, seed: 9);
        var iq = Modulate(deviation, offsetHz: 5_000, carrierOn: InBurst);

        var demod = Demodulate(iq, start => InBurst(start + (BlockSamples / 2)));

        Assert.True(DetectedMode.DigitalUnknown == demod.Mode, $"was {demod.Mode}, score {demod.ModeScore}, offset {demod.CarrierOffsetHz:F0} Hz");
    }

    /// <summary>
    /// Continuous DMR-patterned C4FM — sync every 264 symbols, the real burst cadence — is *named*,
    /// off-bin and all. This is what a hotspot or repeater downlink looks like.
    /// </summary>
    [Fact]
    public void SyncPatternsNameDmrEvenOffBin()
    {
        var script = new List<double>();
        var rng = new Random(21);
        for (var burst = 0; burst < 40; burst++)
        {
            for (var i = 0; i < 120; i++)
            {
                script.Add(rng.Next(4) switch
                {
                    0 => DigitalSignals.DmrOuterHz,
                    1 => DigitalSignals.DmrInnerHz,
                    2 => -DigitalSignals.DmrInnerHz,
                    _ => -DigitalSignals.DmrOuterHz,
                });
            }

            script.AddRange(SyncSymbols);
            for (var i = 0; i < 120; i++)
            {
                script.Add(rng.Next(2) == 0 ? DigitalSignals.DmrOuterHz : -DigitalSignals.DmrOuterHz);
            }
        }

        var deviation = DigitalSignals.C4fmScripted(ChannelRate, 3.0, DigitalSignals.C4fmBaud, script);
        var iq = Modulate(deviation, offsetHz: 2_500);

        var demod = Demodulate(iq, _ => true);

        Assert.True(demod.SyncCount(DetectedMode.Dmr) >= 2, $"only {demod.SyncCount(DetectedMode.Dmr)} sync words recovered");
        Assert.Equal(DetectedMode.Dmr, demod.Mode);
    }

    /// <summary>Four levels without DMR's sync must stay honestly unnamed — the whole point of the histogram's restraint.</summary>
    [Fact]
    public void FourLevelsWithoutSyncStaysUnnamed()
    {
        var deviation = DigitalSignals.C4fm(ChannelRate, 3.0, DigitalSignals.NarrowOuterHz, DigitalSignals.NarrowInnerHz, DigitalSignals.C4fmBaud, seed: 23);
        var iq = Modulate(deviation, offsetHz: 0);

        var demod = Demodulate(iq, _ => true);

        Assert.Equal(0, demod.SyncCount(DetectedMode.Dmr));
        Assert.Equal(DetectedMode.DigitalUnknown, demod.Mode);
    }
}
