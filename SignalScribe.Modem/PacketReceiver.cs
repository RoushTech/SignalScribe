using SignalScribe.Modem.Ax25;
using SignalScribe.Modem.Dsp;

namespace SignalScribe.Modem;

/// <summary>One decoded packet: the frame, and which demodulator profile got there first.</summary>
public sealed record DecodedPacket(Ax25Frame Frame, byte[] Raw, string Profile)
{
    /// <summary>Canonical TNC2 rendering — <c>SRC&gt;DEST,PATH:info</c>.</summary>
    public string Tnc2 => Frame.ToTnc2();
}

/// <summary>
/// The complete AFSK receive path: N demodulator profiles over the same audio, with duplicate
/// decodes collapsed, emitting decoded AX.25 frames.
///
/// Running several profiles at once is the trick Direwolf established — each has different filter
/// and AGC characteristics, so a packet that one slicer mangles another often catches, and a marginal
/// signal yields far more frames than any single demodulator would. They frequently decode the same
/// packet, hence the deduper.
///
/// Replaces upstream DireControl's <c>AfskReceiver</c>, which additionally carried a spectrum
/// analyser and input-level metering for its UI. SignalScribe has its own waterfall and level
/// tracking, so those are left out rather than duplicated.
/// </summary>
public sealed class PacketReceiver
{
    private readonly AfskDemodulator[] _demodulators;

    private readonly FrameDeduper _deduper;

    /// <summary>Raised once per unique CRC-valid frame that also decodes as AX.25.</summary>
    public event Action<DecodedPacket>? PacketReceived;

    public PacketReceiver(IReadOnlyList<DemodProfile> profiles, TimeSpan? dedupWindow = null)
    {
        if (profiles.Count == 0)
        {
            throw new ArgumentException("At least one demodulator profile is required.", nameof(profiles));
        }

        _deduper = new FrameDeduper(dedupWindow);
        _demodulators = new AfskDemodulator[profiles.Count];

        for (var i = 0; i < profiles.Count; i++)
        {
            var profile = profiles[i];
            var demod = new AfskDemodulator(profile);
            demod.FrameDemodulated += raw => OnFrame(raw, profile);
            _demodulators[i] = demod;
        }
    }

    /// <summary>
    /// Two profiles: the standard one, and a variant with a wider pre-filter and slower AGC that
    /// copes better with a weak or off-frequency signal. Two is a deliberate compromise — each costs
    /// a full correlator chain per open gate, and the second roughly doubles that for a worthwhile
    /// gain on marginal packets. More profiles keep helping, but with sharply diminishing returns.
    /// </summary>
    public static PacketReceiver CreateStandard(int sampleRate)
    {
        var standard = DemodProfile.Standard(sampleRate);
        var samplesPerSymbol = sampleRate / 1200f;
        var wide = standard with
        {
            Name = "B",
            PreFilterLowHz = 700f,
            PreFilterHighHz = 2900f,
            CorrelatorTaps = (int)(samplesPerSymbol * 0.8f) | 1,
            AgcAttack = 0.05f,
        };

        return new PacketReceiver([standard, wide]);
    }

    /// <summary>Unique frames emitted so far.</summary>
    public long PacketCount { get; private set; }

    /// <summary>True while any profile reports an HDLC preamble — a real carrier, not a lone noise flag.</summary>
    public bool CarrierDetected => Array.Exists(_demodulators, d => d.CarrierDetected);

    /// <summary>Feeds a block of mono audio. Level is irrelevant; the per-tone AGC adapts.</summary>
    public void ProcessSamples(ReadOnlySpan<float> samples)
    {
        foreach (var demod in _demodulators)
        {
            demod.ProcessSamples(samples);
        }
    }

    private void OnFrame(byte[] raw, DemodProfile profile)
    {
        // The FCS has already passed, so this is a real frame; TryDecode only fails when it is too
        // short for its mandatory address fields.
        if (!Ax25Decoder.TryDecode(raw, out var frame))
        {
            return;
        }

        // DateTime.UtcNow rather than a sample clock: the window only has to be long enough to cover
        // the skew between profiles decoding the same packet, and short enough that a station
        // legitimately repeating a packet is not suppressed.
        if (!_deduper.IsNewFrame(raw, DateTime.UtcNow))
        {
            return;
        }

        PacketCount++;
        PacketReceived?.Invoke(new DecodedPacket(frame, raw, profile.Name));
    }
}
