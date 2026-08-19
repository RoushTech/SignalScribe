namespace SignalScribe.Modem.Dsp;

/// <summary>
/// Fast-attack / slow-decay peak-tracking automatic gain control.  Applied to
/// each tone envelope independently so that mark/space amplitude imbalance
/// ("twist") from emphasis or filtering does not bias the comparator.
/// </summary>
public sealed class Agc(float attack, float decay)
{
    private const float Floor = 1e-6f;

    private float _peak;

    /// <summary>
    /// Tracks the peak of <paramref name="envelope"/> and returns the envelope
    /// normalised by it (≈0–1 for a steady tone).
    /// </summary>
    public float Process(float envelope)
    {
        var rate = envelope > _peak ? attack : decay;
        _peak += (envelope - _peak) * rate;

        return envelope / MathF.Max(_peak, Floor);
    }

    /// <summary>Current tracked peak — a proxy for tone signal strength.</summary>
    public float Peak => _peak;

    public void Reset() => _peak = 0;
}
