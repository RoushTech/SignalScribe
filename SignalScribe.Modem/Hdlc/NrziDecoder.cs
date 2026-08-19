namespace SignalScribe.Modem.Hdlc;

/// <summary>
/// NRZI (non-return-to-zero inverted) decoder as used by AX.25: a level
/// <em>transition</em> between consecutive bit cells encodes a 0, no
/// transition encodes a 1.  Stateful — feed it one demodulated level per bit
/// cell in order.
/// </summary>
public sealed class NrziDecoder
{
    private bool _previousLevel;

    /// <summary>
    /// Decodes the next bit-cell level into a data bit.
    /// </summary>
    public bool Decode(bool level)
    {
        var bit = level == _previousLevel;
        _previousLevel = level;
        return bit;
    }

    public void Reset() => _previousLevel = false;
}
