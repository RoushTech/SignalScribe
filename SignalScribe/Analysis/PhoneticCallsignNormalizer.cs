using System.Text;
using System.Text.RegularExpressions;

namespace SignalScribe.Analysis;

/// <summary>
/// Deterministic phonetic-to-callsign normalization: collapses runs of spoken phonetics
/// ("kilo delta nine alpha bravo charlie") into compact callsigns ("KD9ABC").
/// Pure functions so this is testable without any service scaffolding.
/// </summary>
public static partial class PhoneticCallsignNormalizer
{
    // ITU phonetics plus the variants Whisper actually produces.
    private static readonly Dictionary<string, char> PhoneticMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alpha"] = 'A', ["alfa"] = 'A',
        ["bravo"] = 'B',
        ["charlie"] = 'C', ["charley"] = 'C',
        ["delta"] = 'D',
        ["echo"] = 'E',
        ["foxtrot"] = 'F',
        ["golf"] = 'G',
        ["hotel"] = 'H',
        ["india"] = 'I',
        ["juliet"] = 'J', ["juliett"] = 'J',
        ["kilo"] = 'K',
        ["lima"] = 'L',
        ["mike"] = 'M',
        ["november"] = 'N',
        ["oscar"] = 'O',
        ["papa"] = 'P',
        ["quebec"] = 'Q',
        ["romeo"] = 'R',
        ["sierra"] = 'S',
        ["tango"] = 'T',
        ["uniform"] = 'U',
        ["victor"] = 'V',
        ["whiskey"] = 'W', ["whisky"] = 'W',
        ["xray"] = 'X', ["x-ray"] = 'X',
        ["yankee"] = 'Y',
        ["zulu"] = 'Z',
        ["zero"] = '0', ["oh"] = '0',
        ["one"] = '1',
        ["two"] = '2',
        ["three"] = '3',
        ["four"] = '4',
        ["five"] = '5', ["fife"] = '5',
        ["six"] = '6',
        ["seven"] = '7',
        ["eight"] = '8',
        ["nine"] = '9', ["niner"] = '9',
    };

    // US formats (K/N/W/A prefixes) and general ITU shape: 1-2 letter prefix, digit, 1-4 letter suffix.
    [GeneratedRegex("^[A-Z]{1,2}[0-9][A-Z]{1,4}$")]
    private static partial Regex CallsignShape();

    [GeneratedRegex(@"[^a-zA-Z\-]+")]
    private static partial Regex TokenSeparators();

    /// <summary>
    /// Extracts callsigns spelled phonetically in a transcript. A run of ≥4 consecutive phonetic tokens
    /// that collapses to a valid callsign shape counts as a hit.
    /// </summary>
    public static IReadOnlyList<string> ExtractCallsigns(string transcript)
    {
        var tokens = TokenSeparators().Split(transcript);
        var results = new List<string>();
        var run = new StringBuilder();
        var runLength = 0;

        void FlushRun()
        {
            if (runLength >= 4)
            {
                var candidate = run.ToString();
                if (CallsignShape().IsMatch(candidate))
                {
                    results.Add(candidate);
                }
            }

            run.Clear();
            runLength = 0;
        }

        foreach (var token in tokens)
        {
            if (token.Length > 0 && PhoneticMap.TryGetValue(token, out var mapped))
            {
                run.Append(mapped);
                runLength++;
            }
            else if (token.Length > 0)
            {
                FlushRun();
            }
        }

        FlushRun();
        return results;
    }

    /// <summary>True when the text is already a compact callsign (e.g. Whisper emitted "KD9ABC" directly).</summary>
    public static bool IsCallsign(string text) => CallsignShape().IsMatch(text.ToUpperInvariant());
}
