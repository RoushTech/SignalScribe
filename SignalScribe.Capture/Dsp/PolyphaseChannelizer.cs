using System.Numerics;

namespace SignalScribe.Capture.Dsp;

/// <summary>Receives one hop of channelizer output: M complex channel samples, interleaved (re, im), in DFT bin order.</summary>
public interface IChannelizerSink
{
    void OnHop(ReadOnlySpan<float> channelsInterleaved, long hopIndex);
}

/// <summary>
/// 2×-oversampled polyphase filterbank (weighted overlap-add form). One pass over the wideband IQ
/// stream produces every channel simultaneously: M = fs/spacing channels (power of two), each as a
/// complex stream at 2×spacing (hop M/2), so NBFM voice plus a few kHz of carrier offset fits with
/// filter transition room.
///
/// Hot path discipline: separate I/Q history arrays make the polyphase fold contiguous and
/// Vector&lt;float&gt;-friendly; zero allocations after construction.
///
/// Known v1 limitation (see plan.md): US 2m channels sit on a 5 kHz grid, so a 12.5 kHz filterbank
/// grid leaves up to ±5 kHz carrier offset on some channels. The demodulator's DC-offset removal
/// absorbs the offset; band-edge attenuation on worst-offset channels is accepted for v1 (per-channel
/// fine DDC is the milestone-2 refinement).
/// </summary>
public sealed class PolyphaseChannelizer
{
    private const int TapsPerBranch = 8;

    private readonly int _m;

    private readonly int _hop;

    private readonly float[] _taps;      // reversed prototype, length K*M — index-aligned with history

    private readonly float[] _histI;

    private readonly float[] _histQ;

    private readonly float[] _accI;

    private readonly float[] _accQ;

    private readonly float[] _frame;     // interleaved for FFT

    private readonly float[] _pendingI;

    private readonly float[] _pendingQ;

    private int _pendingCount;

    private long _hopIndex;

    private readonly Fft _fft;

    public PolyphaseChannelizer(double inputSampleRate, double channelSpacingHz)
    {
        var m = (int)Math.Round(inputSampleRate / channelSpacingHz);
        if ((m & (m - 1)) != 0 || Math.Abs(m * channelSpacingHz - inputSampleRate) > 1e-3)
        {
            throw new ArgumentException(
                $"fs/spacing must be a power of two (got {inputSampleRate}/{channelSpacingHz} = {inputSampleRate / channelSpacingHz})");
        }

        _m = m;
        _hop = m / 2;
        InputSampleRate = inputSampleRate;
        ChannelSpacingHz = channelSpacingHz;
        _fft = new Fft(m);

        var n = TapsPerBranch * m;
        var proto = BuildPrototype(n, cutoff: 0.5 / m);
        _taps = new float[n];
        for (var i = 0; i < n; i++)
        {
            _taps[i] = proto[n - 1 - i]; // reversed so taps[i] pairs with hist[i] (hist newest-last)
        }

        _histI = new float[n];
        _histQ = new float[n];
        _accI = new float[m];
        _accQ = new float[m];
        _frame = new float[m * 2];
        _pendingI = new float[_hop];
        _pendingQ = new float[_hop];
    }

    public double InputSampleRate { get; }

    public double ChannelSpacingHz { get; }

    public int ChannelCount => _m;

    public double OutputSampleRate => ChannelSpacingHz * 2;

    /// <summary>Absolute frequency of DFT bin c given the tuner center frequency.</summary>
    public long BinFrequencyHz(int bin, long centerHz) =>
        centerHz + (long)(ChannelSpacingHz * (bin <= _m / 2 ? bin : bin - _m));

    /// <summary>Nearest DFT bin for an absolute frequency, or -1 when outside the span.</summary>
    public int BinForFrequency(long frequencyHz, long centerHz)
    {
        var offset = (double)(frequencyHz - centerHz);
        var bin = (int)Math.Round(offset / ChannelSpacingHz);
        if (Math.Abs(bin) > _m / 2 - 1)
        {
            return -1;
        }

        return bin >= 0 ? bin : bin + _m;
    }

    public void Process(ReadOnlySpan<float> iq, IChannelizerSink sink)
    {
        var offset = 0;
        var total = iq.Length / 2;
        while (offset < total)
        {
            var take = Math.Min(_hop - _pendingCount, total - offset);
            for (var i = 0; i < take; i++)
            {
                _pendingI[_pendingCount + i] = iq[(offset + i) * 2];
                _pendingQ[_pendingCount + i] = iq[(offset + i) * 2 + 1];
            }

            _pendingCount += take;
            offset += take;

            if (_pendingCount == _hop)
            {
                _pendingCount = 0;
                RunHop(sink);
            }
        }
    }

    private void RunHop(IChannelizerSink sink)
    {
        var n = _taps.Length;

        // Slide history left one hop, append the new block (newest at the end).
        Array.Copy(_histI, _hop, _histI, 0, n - _hop);
        Array.Copy(_histQ, _hop, _histQ, 0, n - _hop);
        Array.Copy(_pendingI, 0, _histI, n - _hop, _hop);
        Array.Copy(_pendingQ, 0, _histQ, n - _hop, _hop);

        // Polyphase fold: acc[m] = Σ_k taps[kM+m] * hist[kM+m] — contiguous, vectorized.
        FoldInto(_taps, _histI, _accI);
        FoldInto(_taps, _histQ, _accQ);

        for (var i = 0; i < _m; i++)
        {
            _frame[2 * i] = _accI[i];
            _frame[2 * i + 1] = _accQ[i];
        }

        _fft.Transform(_frame);

        // Hop-phase correction for the M/2 hop: odd hops negate odd bins.
        if ((_hopIndex & 1) == 1)
        {
            for (var c = 1; c < _m; c += 2)
            {
                _frame[2 * c] = -_frame[2 * c];
                _frame[2 * c + 1] = -_frame[2 * c + 1];
            }
        }

        sink.OnHop(_frame, _hopIndex);
        _hopIndex++;
    }

    private void FoldInto(float[] taps, float[] hist, float[] acc)
    {
        var m = _m;
        var vecWidth = Vector<float>.Count;

        for (var seg = 0; seg < TapsPerBranch; seg++)
        {
            var baseIdx = seg * m;
            var i = 0;
            if (seg == 0)
            {
                for (; i <= m - vecWidth; i += vecWidth)
                {
                    (new Vector<float>(taps, baseIdx + i) * new Vector<float>(hist, baseIdx + i)).CopyTo(acc, i);
                }

                for (; i < m; i++)
                {
                    acc[i] = taps[baseIdx + i] * hist[baseIdx + i];
                }
            }
            else
            {
                for (; i <= m - vecWidth; i += vecWidth)
                {
                    var sum = new Vector<float>(acc, i) + new Vector<float>(taps, baseIdx + i) * new Vector<float>(hist, baseIdx + i);
                    sum.CopyTo(acc, i);
                }

                for (; i < m; i++)
                {
                    acc[i] += taps[baseIdx + i] * hist[baseIdx + i];
                }
            }
        }
    }

    /// <summary>Blackman-windowed sinc lowpass, unity DC gain. Cutoff in cycles/sample.</summary>
    private static float[] BuildPrototype(int n, double cutoff)
    {
        var h = new double[n];
        var center = (n - 1) / 2.0;
        double sum = 0;
        for (var i = 0; i < n; i++)
        {
            var x = i - center;
            var sinc = x == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
            var w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1)) + 0.08 * Math.Cos(4 * Math.PI * i / (n - 1));
            h[i] = sinc * w;
            sum += h[i];
        }

        var result = new float[n];
        for (var i = 0; i < n; i++)
        {
            result[i] = (float)(h[i] / sum);
        }

        return result;
    }
}
