namespace SignalScribe.Analysis;

/// <summary>
/// Whisper hallucinates on non-speech audio, most recognisably by echoing its own initial_prompt
/// back as the transcript ("QSL, three, net control, check-in, kerchu..."). Capture's voice
/// measurement keeps most noise away from the model; this catches what slips through (e.g. clips
/// re-queued by hand, where no measurement is available).
/// </summary>
public static class HallucinationFilter
{
    /// <summary>
    /// True when the text is Whisper's own annotation of non-speech audio rather than speech —
    /// "[beeping]" for a courtesy tone, "[BLANK_AUDIO]" for a squelch tail, "(static)". Storing
    /// these as transcripts would feed noise to callsign extraction and speaker clustering.
    /// </summary>
    public static bool IsNonSpeechAnnotation(string text)
    {
        var t = text.Trim();
        if (t.Length < 2)
        {
            return true;
        }

        return (t[0], t[^1]) is ('[', ']') or ('(', ')') or ('*', '*')
            && !t[1..^1].Contains('[')
            && !t[1..^1].Contains('(');
    }

    /// <summary>True when the text is mostly words lifted from the prompt — i.e. the model had nothing to transcribe.</summary>
    public static bool IsPromptEcho(string text, string prompt)
    {
        var words = Tokenize(text);
        if (words.Count == 0)
        {
            return true;
        }

        var promptWords = new HashSet<string>(Tokenize(prompt));
        if (promptWords.Count == 0)
        {
            return false;
        }

        var fromPrompt = 0;
        foreach (var w in words)
        {
            if (promptWords.Contains(w))
            {
                fromPrompt++;
            }
        }

        // Measured on real output: echoes score 0.7-1.0 (fillers like "and the other one" dilute a
        // pure echo), while genuine speech that legitimately uses net vocabulary tops out near 0.2.
        return (double)fromPrompt / words.Count >= 0.6;
    }

    private static List<string> Tokenize(string text)
    {
        var words = new List<string>();
        foreach (var raw in text.Split([' ', ',', '.', '!', '?', '\n', '\r', '\t', ';', ':', '-'], StringSplitOptions.RemoveEmptyEntries))
        {
            var w = raw.Trim().ToLowerInvariant();
            if (w.Length > 1)
            {
                words.Add(w);
            }
        }

        return words;
    }
}
