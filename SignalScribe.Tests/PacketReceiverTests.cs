using SignalScribe.Modem;
using SignalScribe.Modem.Ax25;
using SignalScribe.Modem.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace SignalScribe.Tests;

/// <summary>
/// The vendored soft TNC, driven at SignalScribe's audio rate rather than DireControl's.
///
/// The rate matters: upstream runs a sound card at 44.1 or 48 kHz, and clips here are 16 kHz. That
/// is 13.3 samples per symbol instead of ~40, and <see cref="DemodProfile.Standard"/> derives every
/// filter length from samples-per-symbol, so these pin that the derivation still holds at the low
/// end.
/// </summary>
public class PacketReceiverTests(ITestOutputHelper output)
{
    private const int Rate = 16_000;

    private static byte[] Beacon(string source = "KD9ABC-7", string info = "!4221.55N/08750.12W#PHG5130 test beacon")
        => Ax25Encoder.EncodeUiFrame(source, info, "WIDE1-1,WIDE2-2");

    [Fact]
    public void DecodesAPacketAtSixteenKilohertz()
    {
        var packets = Run(new AfskModulator(Rate).GenerateFrame(Beacon()));

        var packet = Assert.Single(packets);
        Assert.Equal("KD9ABC", packet.Frame.Source.Callsign);
        Assert.Equal(7, packet.Frame.Source.Ssid);
        Assert.Equal("APRS", packet.Frame.Destination.Callsign);
        Assert.Contains("PHG5130 test beacon", packet.Tnc2);
        output.WriteLine($"  {packet.Tnc2}");
    }

    [Fact]
    public void RendersCanonicalTnc2WithDigipeaterPath()
    {
        var packets = Run(new AfskModulator(Rate).GenerateFrame(Beacon()));
        var tnc2 = Assert.Single(packets).Tnc2;

        Assert.StartsWith("KD9ABC-7>APRS,WIDE1-1,WIDE2-2:", tnc2);
    }

    [Fact]
    public void DecodesSeveralFramesInOneKeyup()
    {
        // A digipeater often sends a burst of frames without dropping the carrier, and each must come
        // out separately — the deframer has to re-arm on the flags between them.
        var frames = new List<byte[]>
        {
            Beacon("KD9ABC-1", "!4221.55N/08750.12W#first"),
            Beacon("W9XYZ", ">second one"),
            Beacon("N0CALL-12", ":KD9ABC   :third{01"),
        };

        var packets = Run(new AfskModulator(Rate).GenerateTransmission(frames, leadFlags: 32, tailFlags: 4, amplitude: 0.8f));

        Assert.Equal(3, packets.Count);
        Assert.Equal(["KD9ABC", "W9XYZ", "N0CALL"], packets.Select(p => p.Frame.Source.Callsign));
    }

    [Fact]
    public void CollapsesTheSamePacketDecodedByBothProfiles()
    {
        // Two profiles run over the same audio and usually both succeed on a clean signal. The
        // operator should see one packet, not two.
        var receiver = PacketReceiver.CreateStandard(Rate);
        var seen = new List<DecodedPacket>();
        receiver.PacketReceived += seen.Add;
        receiver.ProcessSamples(new AfskModulator(Rate).GenerateFrame(Beacon()));

        Assert.Single(seen);
    }

    [Fact]
    public void FindsNoPacketsInNoise()
    {
        // The FCS is the real guarantee here, and it is a strong one — but the point is that a band
        // full of hiss must never manufacture a callsign.
        var rng = new Random(4);
        var noise = new float[Rate * 3];
        for (var i = 0; i < noise.Length; i++)
        {
            noise[i] = (float)((rng.NextDouble() * 2) - 1) * 0.5f;
        }

        Assert.Empty(Run(noise));
    }

    [Fact]
    public void FindsNoPacketsInSpeech()
    {
        var speech = new float[Rate * 3];
        for (var i = 0; i < speech.Length; i++)
        {
            var t = i / (double)Rate;
            var envelope = 0.5 + (0.5 * Math.Sin(2 * Math.PI * 4 * t));
            speech[i] = (float)(0.6 * envelope *
                ((0.6 * Math.Sin(2 * Math.PI * 300 * t)) + (0.3 * Math.Sin(2 * Math.PI * 900 * t)) + (0.2 * Math.Sin(2 * Math.PI * 1_700 * t))));
        }

        Assert.Empty(Run(speech));
    }

    [Fact]
    public void RejectsAFrameWhoseBitsWereCorrupted()
    {
        var audio = new AfskModulator(Rate).GenerateFrame(Beacon());

        // Punch a hole in the middle of the packet. The FCS must reject what comes out rather than
        // emitting a frame with a plausible-looking but wrong callsign.
        for (var i = audio.Length / 2; i < (audio.Length / 2) + 400; i++)
        {
            audio[i] = 0;
        }

        Assert.Empty(Run(audio));
    }

    [Theory]
    [InlineData(0.8f)]
    [InlineData(0.05f)]     // a weak signal — the per-tone AGC should still cope
    [InlineData(1.0f)]
    public void CopesWithTheLevelRangeAGateActuallyProduces(float amplitude)
    {
        var packets = Run(new AfskModulator(Rate).GenerateFrame(Beacon(), amplitude: amplitude));
        Assert.Single(packets);
    }

    private static List<DecodedPacket> Run(float[] audio)
    {
        var receiver = PacketReceiver.CreateStandard(Rate);
        var packets = new List<DecodedPacket>();
        receiver.PacketReceived += packets.Add;

        // Feed in blocks, as the capture pipeline does, so nothing depends on seeing it all at once.
        const int Block = 512;
        for (var offset = 0; offset < audio.Length; offset += Block)
        {
            receiver.ProcessSamples(audio.AsSpan(offset, Math.Min(Block, audio.Length - offset)));
        }

        return packets;
    }
}
