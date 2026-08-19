using SignalScribe.Contracts;
using SignalScribe.Enums;

namespace SignalScribe.Capture.Digital;

/// <summary>
/// What a digital framer recovered, in a shape that does not know which mode produced it.
///
/// Every digital voice mode carries its identity and routing in an FEC-protected channel beside the
/// vocoder, so the *fields* differ per mode while the fact of having them does not. Framers
/// therefore report a summary line, the station callsign where the mode names one, and the complete
/// ordered field set — and nothing downstream needs a per-mode branch to store or show it.
/// </summary>
/// <param name="Mode">Which mode's framer produced this; it is also proof the mode is what it says.</param>
/// <param name="Callsign">Transmitting station, when the mode names one, so digital and analog overs join up.</param>
/// <param name="Summary">One-line reading for lists and full-text search.</param>
/// <param name="Fields">Every field decoded, in display order.</param>
public sealed record DecodedHeader(
    DetectedMode Mode,
    string? Callsign,
    string Summary,
    IReadOnlyList<HeaderField> Fields);
