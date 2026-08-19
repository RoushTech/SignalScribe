using SignalScribe.Modem.Hdlc;

namespace SignalScribe.Modem.Dsp;

/// <summary>
/// Bell 202 AFSK modulator: HDLC-frames an AX.25 frame (flags, bit stuffing,
/// FCS), NRZI-encodes it, and renders phase-continuous 1200/2200 Hz audio.
/// Used by the round-trip tests today and by the transmit path in the TX phase.
/// </summary>
public sealed class AfskModulator(int sampleRate, float baud = 1200f, float markFreq = 1200f, float spaceFreq = 2200f)
{
    /// <summary>Flags between frames when several are sent in one keyup.</summary>
    private const int InterFrameFlags = 2;

    /// <summary>
    /// Renders a complete transmission for <paramref name="ax25Frame"/> (raw
    /// frame bytes without FCS) as mono float samples at ±<paramref name="amplitude"/>.
    /// </summary>
    /// <param name="leadFlags">Opening flag count — the TXDelay preamble.</param>
    /// <param name="tailFlags">Closing flag count.</param>
    public float[] GenerateFrame(
        ReadOnlySpan<byte> ax25Frame,
        int leadFlags = 32,
        int tailFlags = 2,
        float amplitude = 0.8f)
    {
        var bits = BuildHdlcBitStream(ax25Frame, leadFlags, tailFlags);
        return RenderBits(bits, amplitude);
    }

    /// <summary>
    /// Renders several frames in one continuous keyup: TXDelay preamble, each
    /// frame separated by a couple of flags, and a single tail.
    /// </summary>
    public float[] GenerateTransmission(
        IReadOnlyList<byte[]> frames,
        int leadFlags,
        int tailFlags,
        float amplitude)
    {
        var bits = new List<bool>();
        for (var i = 0; i < frames.Count; i++)
        {
            bits.AddRange(BuildHdlcBitStream(
                frames[i],
                leadFlags: i == 0 ? leadFlags : InterFrameFlags,
                tailFlags: i == frames.Count - 1 ? tailFlags : 0));
        }
        return RenderBits(bits, amplitude);
    }

    /// <summary>
    /// Renders a TX calibration test tone: cycles through <paramref name="frequencies"/>,
    /// holding each for <paramref name="segmentMs"/>, phase-continuous (no clicks
    /// at segment boundaries), until <paramref name="durationMs"/> of audio at
    /// ±<paramref name="amplitude"/> is produced.  A single frequency yields a
    /// steady tone; two yields an alternating warble.
    /// </summary>
    public float[] GenerateTestTone(
        IReadOnlyList<double> frequencies,
        int durationMs,
        double segmentMs,
        float amplitude = 0.8f)
    {
        if (frequencies.Count == 0 || durationMs <= 0)
            return [];

        var totalSamples = (int)(sampleRate * (durationMs / 1000.0));
        var samplesPerSegment = Math.Max(1, (int)(sampleRate * (segmentMs / 1000.0)));
        var samples = new float[totalSamples];

        var phase = 0.0;
        for (var i = 0; i < totalSamples; i++)
        {
            var freq = frequencies[(i / samplesPerSegment) % frequencies.Count];
            samples[i] = (float)(Math.Sin(phase) * amplitude);
            phase += 2 * Math.PI * freq / sampleRate;
            if (phase > 2 * Math.PI)
                phase -= 2 * Math.PI;
        }

        return samples;
    }

    /// <summary>
    /// Number of flags needed to fill <paramref name="milliseconds"/> of air
    /// time (one flag = 8 bits), at least one.
    /// </summary>
    public int FlagsForMilliseconds(double milliseconds) =>
        Math.Max(1, (int)Math.Ceiling(milliseconds * baud / 8000.0));

    /// <summary>
    /// Builds the HDLC bit stream: lead flags, stuffed frame + FCS, tail flags.
    /// Bits are transmitted LSB first within each octet.
    /// </summary>
    public static List<bool> BuildHdlcBitStream(ReadOnlySpan<byte> ax25Frame, int leadFlags, int tailFlags)
    {
        var bits = new List<bool>((leadFlags + tailFlags) * 8 + (ax25Frame.Length + 2) * 10);

        static void AppendFlag(List<bool> b)
        {
            // 0x7E = 01111110, LSB first: 0,1,1,1,1,1,1,0 — never stuffed.
            b.Add(false);
            for (var i = 0; i < 6; i++)
                b.Add(true);
            b.Add(false);
        }

        for (var i = 0; i < leadFlags; i++)
            AppendFlag(bits);

        var onesCount = 0;
        void AppendStuffed(bool bit)
        {
            bits.Add(bit);
            if (bit)
            {
                if (++onesCount == 5)
                {
                    bits.Add(false); // stuffed zero
                    onesCount = 0;
                }
            }
            else
            {
                onesCount = 0;
            }
        }

        void AppendByteStuffed(byte value)
        {
            for (var i = 0; i < 8; i++)
                AppendStuffed((value & (1 << i)) != 0);
        }

        foreach (var b in ax25Frame)
            AppendByteStuffed(b);

        var fcs = Crc16Ccitt.ComputeFcs(ax25Frame);
        AppendByteStuffed((byte)(fcs & 0xFF));
        AppendByteStuffed((byte)(fcs >> 8));

        for (var i = 0; i < tailFlags; i++)
            AppendFlag(bits);

        return bits;
    }

    /// <summary>
    /// NRZI-encodes the bit stream (0 = tone change, 1 = same tone) and renders
    /// phase-continuous AFSK audio.
    /// </summary>
    private float[] RenderBits(List<bool> bits, float amplitude)
    {
        var samplesPerBit = sampleRate / (double)baud;
        var totalSamples = (int)Math.Ceiling(bits.Count * samplesPerBit);
        var samples = new float[totalSamples];

        var level = true; // NRZI line state; initial value is arbitrary
        var phase = 0.0;
        var sampleIndex = 0;
        var bitBoundary = 0.0;

        foreach (var bit in bits)
        {
            if (!bit)
                level = !level;

            var freq = level ? markFreq : spaceFreq;
            var phaseStep = 2 * Math.PI * freq / sampleRate;

            bitBoundary += samplesPerBit;
            var cellEnd = (int)Math.Round(bitBoundary);

            while (sampleIndex < cellEnd && sampleIndex < totalSamples)
            {
                samples[sampleIndex++] = (float)(Math.Sin(phase) * amplitude);
                phase += phaseStep;
                if (phase > 2 * Math.PI)
                    phase -= 2 * Math.PI;
            }
        }

        return sampleIndex == totalSamples ? samples : samples[..sampleIndex];
    }
}
