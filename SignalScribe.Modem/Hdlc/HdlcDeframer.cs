namespace SignalScribe.Modem.Hdlc;

/// <summary>
/// HDLC deframer: consumes NRZI-decoded data bits, detects 0x7E flags,
/// removes stuffed zero bits, assembles octets (LSB first per AX.25), and
/// emits frames whose CRC-CCITT FCS validates.  Emitted frames exclude the FCS.
/// </summary>
/// <remarks>
/// Bits are matured through an 8-bit delay line so that the bits belonging to
/// a closing flag are never committed to the frame buffer: when the flag
/// pattern is recognised, its 8 bits are exactly the ones still in the delay
/// line and are simply discarded.
/// </remarks>
public sealed class HdlcDeframer
{
    /// <summary>Smallest valid AX.25 frame: 14 address + 1 control + 2 FCS bytes.</summary>
    private const int MinFrameLengthWithFcs = 17;

    /// <summary>Sanity cap well above the AX.25 maximum (256-byte info field).</summary>
    private const int MaxFrameLengthWithFcs = 400;

    private const byte FlagPattern = 0x7E;

    /// <summary>Raised with the frame contents (FCS stripped) when a valid frame is decoded.</summary>
    public event Action<byte[]>? FrameReceived;

    /// <summary>Raised whenever an HDLC flag byte is recognised — useful for DCD.</summary>
    public event Action? FlagDetected;

    /// <summary>True after an opening flag until the frame ends or aborts.</summary>
    public bool InFrame => _inFrame;

    /// <summary>Total frames emitted with a valid FCS.</summary>
    public long ValidFrameCount { get; private set; }

    /// <summary>Frames dropped due to bad FCS, bad length, or overflow.</summary>
    public long InvalidFrameCount { get; private set; }

    /// <summary>
    /// Opening flags required before a bad-CRC candidate counts as a damaged
    /// packet.  Real transmissions start with a TXDelay preamble of many
    /// flags; random noise essentially never produces two in a row, so this
    /// keeps the damaged-packet counter meaningful on a noisy channel.
    /// </summary>
    private const int MinOpeningFlagsForDamageCount = 2;

    // 8-bit delay line; newest bit enters at bit 7, the matured bit leaves from bit 0.
    private byte _delayLine;
    private int _delayCount;

    // Consecutive flags immediately preceding the current frame's content.
    private int _flagRun;

    // Whether any bit matured since the last flag — aborts clear the assembly
    // buffers, so this is tracked explicitly to keep the flag run honest.
    private bool _dataSinceFlag;

    // Destuffing / octet assembly state for matured bits.
    private bool _inFrame;
    private int _onesCount;
    private byte _currentByte;
    private int _bitPosition;
    private readonly List<byte> _frame = new(MaxFrameLengthWithFcs);

    /// <summary>
    /// Processes one NRZI-decoded data bit.
    /// </summary>
    public void ProcessBit(bool bit)
    {
        // Capture the oldest bit before it is shifted out, but only consume it
        // once we know the newest 8 bits are not a flag.
        var maturedBit = (_delayLine & 0x01) != 0;
        var delayWasFull = _delayCount >= 8;

        _delayLine = (byte)((_delayLine >> 1) | (bit ? 0x80 : 0x00));
        if (_delayCount < 8)
            _delayCount++;

        if (_delayCount == 8 && _delayLine == FlagPattern)
        {
            // Flag: the bit that matured on this push still belongs to the
            // frame — consume it, then everything matured is the candidate
            // frame and the 8 flag bits in the delay line are discarded.
            if (delayWasFull)
                ConsumeMaturedBit(maturedBit);
            EndFrame();
            _flagRun = _dataSinceFlag ? 1 : _flagRun + 1;
            _dataSinceFlag = false;
            _delayCount = 0;
            _inFrame = true;
            ResetAssembly();
            FlagDetected?.Invoke();
            return;
        }

        if (delayWasFull)
            ConsumeMaturedBit(maturedBit);
    }

    /// <summary>
    /// Drops any partial frame and resets all state (e.g. on DCD loss).
    /// </summary>
    public void Reset()
    {
        _inFrame = false;
        _delayLine = 0;
        _delayCount = 0;
        _flagRun = 0;
        _dataSinceFlag = false;
        ResetAssembly();
    }

    private void ConsumeMaturedBit(bool bit)
    {
        _dataSinceFlag = true;

        if (!_inFrame)
            return;

        if (bit)
        {
            _onesCount++;
            if (_onesCount >= 7)
            {
                // Abort sequence — discard the partial frame and hunt for a flag.
                _inFrame = false;
                ResetAssembly();
                return;
            }

            AppendBit(true);
            return;
        }

        if (_onesCount == 5)
        {
            // Stuffed zero — discard it.
            _onesCount = 0;
            return;
        }

        _onesCount = 0;
        AppendBit(false);
    }

    private void AppendBit(bool bit)
    {
        if (bit)
            _currentByte |= (byte)(1 << _bitPosition);

        if (++_bitPosition == 8)
        {
            if (_frame.Count >= MaxFrameLengthWithFcs)
            {
                // Runaway "frame" (noise with no closing flag) — give up on it
                // silently; this is channel noise, not a damaged packet.
                _inFrame = false;
                ResetAssembly();
                return;
            }

            _frame.Add(_currentByte);
            _currentByte = 0;
            _bitPosition = 0;
        }
    }

    private void EndFrame()
    {
        if (!_inFrame)
            return;

        // A valid frame is a whole number of octets within AX.25 size limits
        // and must pass the FCS check.
        var plausible = _bitPosition == 0 &&
            _frame.Count is >= MinFrameLengthWithFcs and <= MaxFrameLengthWithFcs;

        if (plausible &&
            Crc16Ccitt.IsFrameValid(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_frame)))
        {
            ValidFrameCount++;
            FrameReceived?.Invoke(_frame[..^2].ToArray());
        }
        else if (plausible && _flagRun >= MinOpeningFlagsForDamageCount)
        {
            // Right shape, wrong checksum, arrived behind a real preamble —
            // a genuinely damaged packet.  Anything short, misaligned, or
            // without consecutive opening flags is just channel noise between
            // spurious flag patterns and is not worth counting.
            InvalidFrameCount++;
        }
    }

    private void ResetAssembly()
    {
        _onesCount = 0;
        _currentByte = 0;
        _bitPosition = 0;
        _frame.Clear();
    }
}
