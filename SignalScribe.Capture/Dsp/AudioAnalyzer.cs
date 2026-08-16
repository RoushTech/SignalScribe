namespace SignalScribe.Capture.Dsp;

/// <summary>
/// Per-transmission audio-domain analysis over the demodulated 16 kHz stream:
///  - voice-likeness (channel auto-creation gate): speech-band energy ratio + syllabic envelope modulation + duration
///  - tone events: short pure tone → courtesy tone; sustained pure tone → heterodyne (double)
///  - squelch-crash heuristic: broadband high-power burst near the clip end
/// Heuristic thresholds are first-cut; tune against real air via IQ replay fixtures.
/// </summary>
public sealed class AudioAnalyzer
{
    private const int SampleRate = 16_000;

    private const int FftSize = 512;

    private const int EnvelopeBlock = 160; // 10 ms

    private readonly Fft _fft = new(FftSize);

    private readonly float[] _fftFrame = new float[FftSize * 2];

    private readonly float[] _window = BuildHann(FftSize);

    private int _fftFill;

    private readonly List<float> _envelopeRms = new(1024);

    private double _envBlockSum;

    private int _envBlockCount;

    private double _speechBandEnergy;

    private double _totalEnergy;

    private int _framesTotal;

    private int _framesVoiced;

    // Tone tracking
    private int _toneRunFrames;

    private int _toneBin = -1;

    private long _samplesSeen;

    public record ToneEvent(int OffsetMs, int DurationMs, bool Sustained);

    public List<ToneEvent> ToneEvents { get; } = [];

    public int DurationMs => (int)(_samplesSeen * 1000 / SampleRate);

    public void FeedShorts(ReadOnlySpan<short> pcm)
    {
        Span<float> block = stackalloc float[256];
        var offset = 0;
        while (offset < pcm.Length)
        {
            var take = Math.Min(block.Length, pcm.Length - offset);
            for (var i = 0; i < take; i++)
            {
                block[i] = pcm[offset + i] / 32768f;
            }

            Feed(block[..take]);
            offset += take;
        }
    }

    public void Feed(ReadOnlySpan<float> pcm)
    {
        foreach (var sample in pcm)
        {
            _samplesSeen++;
            _envBlockSum += sample * sample;
            if (++_envBlockCount >= EnvelopeBlock)
            {
                _envelopeRms.Add(MathF.Sqrt((float)(_envBlockSum / _envBlockCount)));
                _envBlockSum = 0;
                _envBlockCount = 0;
            }

            _fftFrame[2 * _fftFill] = sample;
            _fftFrame[2 * _fftFill + 1] = 0;
            if (++_fftFill >= FftSize)
            {
                _fftFill = 0;
                AnalyzeFrame();
            }
        }
    }

    private void AnalyzeFrame()
    {
        for (var i = 0; i < FftSize; i++)
        {
            _fftFrame[2 * i] *= _window[i];
            _fftFrame[2 * i + 1] = 0;
        }

        _fft.Transform(_fftFrame);

        double total = 0, speech = 0, peak = 0;
        var peakBin = 0;
        var binHz = (double)SampleRate / FftSize; // 31.25 Hz
        for (var b = 1; b < FftSize / 2; b++)
        {
            var p = _fftFrame[2 * b] * _fftFrame[2 * b] + _fftFrame[2 * b + 1] * _fftFrame[2 * b + 1];
            total += p;
            var hz = b * binHz;
            if (hz is >= 300 and <= 2700)
            {
                speech += p;
            }

            if (p > peak)
            {
                peak = p;
                peakBin = b;
            }
        }

        _totalEnergy += total;
        _speechBandEnergy += speech;
        _framesTotal++;
        if (total > 1e-7 && speech / total > 0.55)
        {
            // Voiced frame: speech-band dominated. Squelch-tail FM noise is broadband (~30% in-band)
            // and never counts — so the tail can't dilute the verdict (frame = 32 ms).
            _framesVoiced++;
        }

        // Pure-tone frame: one bin (±1) dominates.
        var neighborhood = peak;
        if (peakBin > 1)
        {
            neighborhood += _fftFrame[2 * (peakBin - 1)] * _fftFrame[2 * (peakBin - 1)] + _fftFrame[2 * (peakBin - 1) + 1] * _fftFrame[2 * (peakBin - 1) + 1];
        }

        if (peakBin < FftSize / 2 - 1)
        {
            neighborhood += _fftFrame[2 * (peakBin + 1)] * _fftFrame[2 * (peakBin + 1)] + _fftFrame[2 * (peakBin + 1) + 1] * _fftFrame[2 * (peakBin + 1) + 1];
        }

        // 0.9: a real heterodyne beat or courtesy tone is essentially single-bin pure; voiced speech
        // (even de-emphasized, low-pitched speech) doesn't hold 90% of energy in a 94 Hz neighborhood.
        var isPure = total > 1e-6 && neighborhood / total > 0.9;
        var sameTone = isPure && (_toneBin < 0 || Math.Abs(peakBin - _toneBin) <= 1);

        if (isPure && sameTone)
        {
            if (_toneRunFrames == 0)
            {
                _toneBin = peakBin;
            }

            _toneRunFrames++;
        }
        else
        {
            FlushToneRun();
            if (isPure)
            {
                _toneBin = peakBin;
                _toneRunFrames = 1;
            }
        }
    }

    private void FlushToneRun()
    {
        if (_toneRunFrames > 0)
        {
            var frameMs = FftSize * 1000 / SampleRate; // 32 ms
            var durationMs = _toneRunFrames * frameMs;
            var endMs = (int)(_samplesSeen * 1000 / SampleRate);
            if (durationMs >= 60)
            {
                ToneEvents.Add(new ToneEvent(Math.Max(0, endMs - durationMs), durationMs, durationMs >= 500));
            }
        }

        _toneRunFrames = 0;
        _toneBin = -1;
    }

    /// <summary>Finalize and compute verdicts. Call once, at squelch close.</summary>
    public AudioVerdict Finish()
    {
        FlushToneRun();

        var speechRatio = _totalEnergy > 1e-9 ? _speechBandEnergy / _totalEnergy : 0;

        // Syllabic modulation: coefficient of variation of the 10 ms envelope, plus 2–8 Hz peak rate.
        double modDepth = 0;
        var peakRateHz = 0.0;
        if (_envelopeRms.Count >= 30)
        {
            double mean = 0;
            foreach (var e in _envelopeRms)
            {
                mean += e;
            }

            mean /= _envelopeRms.Count;
            double var = 0;
            foreach (var e in _envelopeRms)
            {
                var += (e - mean) * (e - mean);
            }

            var /= _envelopeRms.Count;
            modDepth = mean > 1e-6 ? Math.Sqrt(var) / mean : 0;

            // Envelope peaks (local maxima above mean) per second ≈ syllable rate.
            var peaks = 0;
            for (var i = 1; i < _envelopeRms.Count - 1; i++)
            {
                if (_envelopeRms[i] > mean && _envelopeRms[i] >= _envelopeRms[i - 1] && _envelopeRms[i] > _envelopeRms[i + 1])
                {
                    peaks++;
                }
            }

            peakRateHz = peaks * 100.0 / _envelopeRms.Count; // blocks are 10 ms
        }

        var sustainedTone = ToneEvents.Exists(t => t.Sustained);
        var voicedMs = _framesVoiced * FftSize * 1000 / SampleRate;
        var voiceLike = voicedMs >= 800
            && modDepth > 0.30
            && peakRateHz is >= 1.5 and <= 12
            && !sustainedTone;

        return new AudioVerdict(voiceLike, speechRatio, voicedMs, modDepth, peakRateHz, sustainedTone);
    }

    private static float[] BuildHann(int n)
    {
        var w = new float[n];
        for (var i = 0; i < n; i++)
        {
            w[i] = 0.5f * (1 - MathF.Cos(2 * MathF.PI * i / n));
        }

        return w;
    }
}

public record AudioVerdict(bool VoiceLike, double SpeechBandRatio, int VoicedMs, double ModulationDepth, double SyllableRateHz, bool SustainedTone);
