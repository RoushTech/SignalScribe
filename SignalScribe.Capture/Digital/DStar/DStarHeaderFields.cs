using SignalScribe.Contracts;
using SignalScribe.Enums;

namespace SignalScribe.Capture.Digital.DStar;

/// <summary>
/// Renders a decoded D-STAR header into the mode-agnostic <see cref="DecodedHeader"/> — the whole
/// header, not the summary line.
///
/// The header is the entire content of a D-STAR transmission as far as this project can read it: the
/// voice needs AMBE and does not get decoded, so routing, call type and the emergency flag are all
/// the operator will ever have. Reporting only "who called whom" would throw away the rest of what
/// arrived intact and CRC-checked.
/// </summary>
public static class DStarHeaderFields
{
    public static DecodedHeader Describe(DStarHeader header)
    {
        var fields = new List<HeaderField>(8)
        {
            new("My call", header.Mycall),
        };

        if (!string.IsNullOrWhiteSpace(header.MycallSuffix))
        {
            fields.Add(new HeaderField("My call suffix", header.MycallSuffix));
        }

        fields.Add(new HeaderField("Your call", header.Urcall));

        if (!string.IsNullOrWhiteSpace(header.RepeaterSource))
        {
            fields.Add(new HeaderField("Repeater in", header.RepeaterSource));
        }

        if (!string.IsNullOrWhiteSpace(header.RepeaterTarget))
        {
            fields.Add(new HeaderField("Repeater out", header.RepeaterTarget));
        }

        fields.Add(new HeaderField("Call type", header.IsGroupCall ? "Group call" : "Directed call"));
        fields.Add(new HeaderField("Emergency", header.IsEmergency ? "Yes" : "No"));

        // The raw flag bytes: bit 3 of the first is the emergency flag, and the rest carry repeater
        // control and data-mode bits that this project does not interpret yet. Shown as hex rather
        // than dropped, because an operator chasing an oddity can read them and we cannot.
        fields.Add(new HeaderField("Flags", string.Join(' ', header.Flags.Select(f => f.ToString("X2")))));

        return new DecodedHeader(DetectedMode.DStar, header.Mycall, Summarize(header), fields);
    }

    /// <summary>
    /// Who called whom, written the way operators say it, and only mentioning the repeater path when
    /// there is one so a simplex contact does not carry empty fields.
    /// </summary>
    private static string Summarize(DStarHeader header)
    {
        var line = $"{header.MycallWithSuffix} → {header.Urcall}";

        if (!string.IsNullOrWhiteSpace(header.RepeaterSource))
        {
            line += $" via {header.RepeaterSource}";
            if (!string.IsNullOrWhiteSpace(header.RepeaterTarget) && header.RepeaterTarget != header.RepeaterSource)
            {
                line += $" → {header.RepeaterTarget}";
            }
        }

        return header.IsEmergency ? $"EMERGENCY — {line}" : line;
    }
}
