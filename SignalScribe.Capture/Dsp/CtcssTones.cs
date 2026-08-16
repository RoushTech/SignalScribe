namespace SignalScribe.Capture.Dsp;

/// <summary>The 50 standard CTCSS tones, in Hz.</summary>
public static class CtcssTones
{
    /// <summary>
    /// EIA/TIA standard set. Neighbours get as close as 1.5 Hz apart (159.8 / 162.2 / 165.5), which
    /// sets the resolution the detector needs: about 0.5 Hz, so roughly two seconds of audio.
    /// </summary>
    public static readonly double[] All =
    [
        67.0, 69.3, 71.9, 74.4, 77.0, 79.7, 82.5, 85.4, 88.5, 91.5,
        94.8, 97.4, 100.0, 103.5, 107.2, 110.9, 114.8, 118.8, 123.0, 127.3,
        131.8, 136.5, 141.3, 146.2, 151.4, 156.7, 159.8, 162.2, 165.5, 167.9,
        171.3, 173.8, 177.3, 179.9, 183.5, 186.2, 189.9, 192.8, 196.6, 199.5,
        203.5, 206.5, 210.7, 218.1, 225.7, 229.1, 233.6, 241.8, 250.3, 254.1,
    ];

    /// <summary>Closest standard tone to a measured frequency, or null if nothing is within <paramref name="toleranceHz"/>.</summary>
    public static double? Nearest(double hz, double toleranceHz = 1.0)
    {
        var best = double.MaxValue;
        double? match = null;
        foreach (var t in All)
        {
            var d = Math.Abs(t - hz);
            if (d < best)
            {
                best = d;
                match = t;
            }
        }

        return best <= toleranceHz ? match : null;
    }
}
