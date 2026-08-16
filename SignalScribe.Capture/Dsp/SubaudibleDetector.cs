namespace SignalScribe.Capture.Dsp;

/// <summary>
/// Reads the squelch tone riding under the voice: CTCSS by measuring its frequency, DCS by decoding
/// its bitstream. Fed the discriminator output *before* the audio high-pass, because that filter
/// exists precisely to throw this away.
///
/// Both live below 300 Hz, so everything runs on a decimated ~1 kHz stream — 50 Goertzel filters
/// and a bit slicer at that rate cost nothing next to the channelizer.
/// </summary>
public sealed class SubaudibleDetector
{
    /// <summary>Neighbouring CTCSS tones are 1.5 Hz apart, so a usable answer needs about this much audio.</summary>
    public const double MinSeconds = 1.0;

    private const double TargetRate = 1_000;

    private const double DcsBitRate = 134.4;

    private readonly int _decimation;

    private readonly double _rate;

    private readonly Biquad _antiAlias;

    /// <summary>
    /// Goertzel triplets per standard tone: the tone itself, and guards 1.2 Hz either side. A real
    /// CTCSS tone peaks at the standard frequency; mains hum at 120 Hz sits 1.2 Hz off 118.8 and
    /// would otherwise be reported as that tone, because the nearest-bin answer is always *some*
    /// tone. Requiring the centre to beat both guards is what makes "no tone" an available answer.
    /// </summary>
    private const double GuardOffsetHz = 1.2;

    private readonly double[] _coeff;

    private readonly double[] _s1;

    private readonly double[] _s2;

    private readonly double[] _guardCoeff;

    private readonly double[] _g1;

    private readonly double[] _g2;

    private readonly List<int> _bits = [];

    private double _energy;

    private long _samples;

    private int _decimCount;

    private double _decimSum;

    // DCS bit clock: 134.4 bps against a 1 kHz stream is 7.44 samples a bit, so the phase is fractional.
    private double _bitPhase;

    private double _bitSum;

    private int _bitSamples;

    private double _slicerMean;

    public SubaudibleDetector(double inputSampleRate)
    {
        _decimation = Math.Max(1, (int)Math.Round(inputSampleRate / TargetRate));
        _rate = inputSampleRate / _decimation;
        _antiAlias = Biquad.LowPass(300, inputSampleRate);

        _coeff = new double[CtcssTones.All.Length];
        _s1 = new double[CtcssTones.All.Length];
        _s2 = new double[CtcssTones.All.Length];
        _guardCoeff = new double[CtcssTones.All.Length * 2];
        _g1 = new double[_guardCoeff.Length];
        _g2 = new double[_guardCoeff.Length];
        for (var i = 0; i < _coeff.Length; i++)
        {
            _coeff[i] = 2 * Math.Cos(2 * Math.PI * CtcssTones.All[i] / _rate);
            _guardCoeff[2 * i] = 2 * Math.Cos(2 * Math.PI * (CtcssTones.All[i] - GuardOffsetHz) / _rate);
            _guardCoeff[(2 * i) + 1] = 2 * Math.Cos(2 * Math.PI * (CtcssTones.All[i] + GuardOffsetHz) / _rate);
        }
    }

    /// <summary>Diagnostics from the last CTCSS evaluation: winner, margin over the runner-up, peakiness vs guards, share of band energy.</summary>
    public (double Tone, double Margin, double Peak, double Share) LastScore { get; private set; }

    /// <summary>Seconds of audio seen so far — below <see cref="MinSeconds"/> no verdict is offered.</summary>
    public double Seconds => _samples / _rate;

    /// <summary>Feed one discriminator sample, DC-removed, at the demodulator's input rate.</summary>
    public void Feed(float sample)
    {
        var low = _antiAlias.Process(sample);
        _decimSum += low;
        if (++_decimCount < _decimation)
        {
            return;
        }

        var x = _decimSum / _decimCount;
        _decimCount = 0;
        _decimSum = 0;

        _samples++;
        _energy += x * x;

        for (var i = 0; i < _coeff.Length; i++)
        {
            var s0 = x + (_coeff[i] * _s1[i]) - _s2[i];
            _s2[i] = _s1[i];
            _s1[i] = s0;
        }

        for (var i = 0; i < _guardCoeff.Length; i++)
        {
            var g0 = x + (_guardCoeff[i] * _g1[i]) - _g2[i];
            _g2[i] = _g1[i];
            _g1[i] = g0;
        }

        // DCS: integrate over each bit period, then slice against a slowly tracked mean.
        _slicerMean += 0.001 * (x - _slicerMean);
        _bitSum += x - _slicerMean;
        _bitSamples++;
        _bitPhase += DcsBitRate / _rate;
        if (_bitPhase >= 1.0)
        {
            _bitPhase -= 1.0;
            _bits.Add(_bitSum >= 0 ? 1 : 0);
            _bitSum = 0;
            _bitSamples = 0;
        }
    }

    /// <summary>
    /// The CTCSS tone present, or null. Requires the winner to hold a real share of the sub-audible
    /// energy and to beat its nearest rival, so an off-standard hum is reported as nothing rather
    /// than as whichever tone it happens to sit closest to.
    /// </summary>
    public double? Ctcss()
    {
        if (Seconds < MinSeconds || _energy <= 1e-12)
        {
            return null;
        }

        double best = 0, second = 0;
        var bestIndex = -1;
        for (var i = 0; i < _coeff.Length; i++)
        {
            var power = Power(_coeff[i], _s1[i], _s2[i]);
            if (power > best)
            {
                second = best;
                best = power;
                bestIndex = i;
            }
            else if (power > second)
            {
                second = power;
            }
        }

        if (bestIndex < 0)
        {
            return null;
        }

        // The winner must be a peak in its own right, not just the closest standard tone to whatever
        // is loudest down there: it has to beat its nearest rival, and beat guards 1.2 Hz either
        // side of itself. Real voice has plenty of energy below 300 Hz, so an absolute share of the
        // band is a poor test — 146.640 carries CTCSS 146.2 and was reported as no tone at all.
        var low = Power(_guardCoeff[2 * bestIndex], _g1[2 * bestIndex], _g2[2 * bestIndex]);
        var high = Power(_guardCoeff[(2 * bestIndex) + 1], _g1[(2 * bestIndex) + 1], _g2[(2 * bestIndex) + 1]);

        // Calibrated against speech that has a pitch fundamental wandering through the CTCSS band,
        // which is the thing a tone actually has to be told apart from. Measured either side:
        // real tones scored margin >=39, peak >=22, share >=0.18; hum, plain speech and a DCS
        // bitstream all scored margin <=1.5, peak <=3.5, share <=0.006. All three must hold, each
        // with several times that separation, so no single measure carries the decision.
        var guard = Math.Max(low, high);
        var margin = best / Math.Max(1e-12, second);
        var peak = best / Math.Max(1e-12, guard);
        var share = best / Math.Max(1e-12, _energy * _samples / 2);

        LastScore = (CtcssTones.All[bestIndex], margin, peak, share);
        return margin > 8 && peak > 6 && share > 0.05 ? CtcssTones.All[bestIndex] : null;
    }

    private static double Power(double coeff, double s1, double s2) => (s1 * s1) + (s2 * s2) - (coeff * s1 * s2);

    /// <summary>
    /// The DCS code present, or null. The word repeats forever, so the whole bitstream is scored at
    /// every offset and the winner has to dominate: a handful of accidentally-valid words is what a
    /// hum sliced at 134.4 bps produces, and DCS repeaters are rare enough that a weak match is far
    /// more likely to be noise than a real code.
    /// </summary>
    public int? Dcs()
    {
        if (Ctcss() is not null)
        {
            return null; // a CTCSS tone beats against the bit clock into a convincing fake codeword
        }

        const int Word = 23;
        const int MinAgreeing = 5;       // ~0.9 s of signal; a real code decodes every word
        const double MinShare = 0.6;     // and dominates the words at its offset

        if (_bits.Count < Word * MinAgreeing)
        {
            return null;
        }

        int? bestCode = null;
        var bestCount = 0;
        for (var offset = 0; offset < Word; offset++)
        {
            var votes = new Dictionary<int, int>();
            var words = 0;
            for (var start = offset; start + Word <= _bits.Count; start += Word)
            {
                words++;
                var word = 0;
                for (var b = 0; b < Word; b++)
                {
                    word |= _bits[start + b] << b;
                }

                if (DcsCodes.Decode(word) is { } code)
                {
                    votes[code] = votes.GetValueOrDefault(code) + 1;
                }
            }

            foreach (var (code, count) in votes)
            {
                if (count >= MinAgreeing && count >= words * MinShare && count > bestCount)
                {
                    bestCode = code;
                    bestCount = count;
                }
            }
        }

        return bestCode;
    }
}
