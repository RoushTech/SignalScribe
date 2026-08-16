using Microsoft.Extensions.Logging;
using static SignalScribe.Capture.Sources.SdrPlayInterop;

namespace SignalScribe.Capture.Sources;

/// <summary>
/// RSP source over sdrplay_api v3.15 (all RSP models). The native stream callback copies I/Q
/// shorts into a preallocated 2-second ring buffer; Read() drains it as normalized floats. GC
/// discipline: the callback allocates nothing, the ring is pinned-by-construction (large array),
/// and pauses are absorbed by ring depth (see plan.md — GC discussion).
/// </summary>
public sealed class SdrPlaySource : ISampleSource
{
    private readonly ILogger _logger;

    private readonly string? _requestedSerial;

    // 2 seconds of interleaved I/Q shorts.
    private readonly short[] _ring;

    private readonly object _ringLock = new();

    private int _ringWrite;

    private int _ringRead;

    private int _ringCount;

    private long _sampleCounter;

    private long _adcOverloads;

    private DeviceT _device;

    private bool _selected;

    private bool _initialised;

    // Keep delegate instances alive for the lifetime of the stream — the native side holds raw pointers.
    private StreamCallback? _streamCb;

    private StreamCallback? _streamCbB;

    private EventCallback? _eventCb;

    public SdrPlaySource(double sampleRate, long centerFrequencyHz, string? deviceSerial, int gainReductionDb, int lnaState, bool agcEnabled, ILogger logger)
    {
        SampleRate = sampleRate;
        CenterFrequencyHz = centerFrequencyHz;
        _requestedSerial = deviceSerial;
        GainReductionDb = gainReductionDb;
        LnaState = lnaState;
        AgcEnabled = agcEnabled;
        _logger = logger;
        _ring = new short[(int)(sampleRate * 2) * 2];
    }

    public double SampleRate { get; }

    public long CenterFrequencyHz { get; }

    public int GainReductionDb { get; }

    public int LnaState { get; }

    public bool AgcEnabled { get; }

    public string? ActiveSerial { get; private set; }

    public string? ActiveModel { get; private set; }

    public long SampleCounter => Interlocked.Read(ref _sampleCounter);

    public long AdcOverloads => Interlocked.Read(ref _adcOverloads);

    public unsafe void Start()
    {
        Check(Open(), "Open");
        Check(LockDeviceApi(), "LockDeviceApi");
        try
        {
            var devices = stackalloc DeviceT[MaxDevices];
            Check(GetDevices(devices, out var count, MaxDevices), "GetDevices");

            var index = -1;
            for (var i = 0; i < count; i++)
            {
                if (devices[i].Valid == 0)
                {
                    continue; // held by another application
                }

                if (_requestedSerial is null || devices[i].Serial == _requestedSerial)
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                throw new InvalidOperationException(_requestedSerial is null
                    ? $"No available SDRplay device (found {count}, all in use or none attached)."
                    : $"SDRplay device with serial '{_requestedSerial}' not found or in use.");
            }

            _device = devices[index];
            Check(SelectDevice(ref _device), "SelectDevice");
            _selected = true;
            ActiveSerial = _device.Serial;
            ActiveModel = ModelName(_device.HwVer);
        }
        finally
        {
            UnlockDeviceApi();
        }

        Check(GetDeviceParams(_device.Dev, out var deviceParams), "GetDeviceParams");
        deviceParams->DevParams->FsFreq.FsHz = SampleRate;

        var rx = deviceParams->RxChannelA;
        rx->TunerParams.RfFreq.RfHz = CenterFrequencyHz;
        rx->TunerParams.BwType = BwForSampleRate(SampleRate);
        rx->TunerParams.IfType = 0;   // zero-IF
        rx->TunerParams.LoMode = 1;   // auto
        rx->TunerParams.Gain.GRdB = Math.Clamp(GainReductionDb, 20, 59);
        rx->TunerParams.Gain.LnaState = (byte)Math.Clamp(LnaState, 0, 9);
        rx->CtrlParams.Agc.Enable = AgcEnabled ? 2 : 0; // 50 Hz loop / off
        rx->CtrlParams.Agc.SetPointDbfs = -30;
        rx->CtrlParams.Decimation.Enable = 0;

        _streamCb = OnStreamA;
        _streamCbB = OnStreamB;
        _eventCb = OnEvent;
        var fns = new CallbackFnsT
        {
            StreamACbFn = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_streamCb),
            StreamBCbFn = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_streamCbB),
            EventCbFn = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_eventCb),
        };

        Check(Init(_device.Dev, ref fns, IntPtr.Zero), "Init");
        _initialised = true;
        _logger.LogInformation(
            "{Model} {Serial} streaming: {Rate} SPS @ {Center} Hz, BW {Bw} kHz, gRdB {Gr}, LNA {Lna}, AGC {Agc}",
            ActiveModel, ActiveSerial, SampleRate, CenterFrequencyHz,
            BwForSampleRate(SampleRate), GainReductionDb, LnaState, AgcEnabled);
    }

    public void Stop()
    {
        if (_initialised)
        {
            Uninit(_device.Dev);
            _initialised = false;
        }

        if (_selected)
        {
            LockDeviceApi();
            ReleaseDevice(ref _device);
            UnlockDeviceApi();
            _selected = false;
        }
    }

    public int Read(Span<float> iq)
    {
        // Blocks briefly until samples arrive; returns 0 only when stopped.
        for (var wait = 0; wait < 200; wait++)
        {
            lock (_ringLock)
            {
                if (_ringCount > 0)
                {
                    var take = Math.Min(_ringCount, iq.Length);
                    for (var i = 0; i < take; i++)
                    {
                        iq[i] = _ring[_ringRead] / 32768f;
                        _ringRead = (_ringRead + 1) % _ring.Length;
                    }

                    _ringCount -= take;
                    return take;
                }

                if (!_initialised)
                {
                    return 0;
                }
            }

            Thread.Sleep(5);
        }

        return _initialised ? -1 : 0; // -1 = timed out with device supposedly streaming → stall watchdog fires
    }

    public void Dispose() => Stop();

    private unsafe void OnStreamA(short* xi, short* xq, StreamCbParamsT* cbParams, uint numSamples, uint reset, IntPtr ctx)
    {
        Interlocked.Add(ref _sampleCounter, numSamples);
        lock (_ringLock)
        {
            var space = (_ring.Length - _ringCount) / 2;
            var n = (int)Math.Min(numSamples, space); // overflow drops newest — counter still advances, watchdog sees liveness
            for (var i = 0; i < n; i++)
            {
                _ring[_ringWrite] = xi[i];
                _ring[(_ringWrite + 1) % _ring.Length] = xq[i];
                _ringWrite = (_ringWrite + 2) % _ring.Length;
            }

            _ringCount += n * 2;
        }
    }

    private unsafe void OnStreamB(short* xi, short* xq, StreamCbParamsT* cbParams, uint numSamples, uint reset, IntPtr ctx)
    {
        // Single-tuner operation; RSPduo slave stream unused.
    }

    private void OnEvent(SdrEvent eventId, TunerSelect tuner, IntPtr eventParams, IntPtr ctx)
    {
        switch (eventId)
        {
            case SdrEvent.PowerOverloadChange:
                Interlocked.Increment(ref _adcOverloads);
                // Mandatory ack, or the service keeps re-raising the event.
                Update(_device.Dev, tuner, ReasonForUpdate.Ctrl_OverloadMsgAck, 0);
                break;
            case SdrEvent.DeviceRemoved:
            case SdrEvent.DeviceFailure:
                _logger.LogError("sdrplay event {Event} — stream is dead, watchdog will re-init", eventId);
                _initialised = false;
                break;
        }
    }

    /// <summary>Largest analog bandwidth (kHz) that fits alias-free inside the sample rate.</summary>
    private static int BwForSampleRate(double fs) => fs switch
    {
        >= 8_000_000 => 7000,
        >= 7_000_000 => 6000,
        >= 6_000_000 => 5000,
        >= 5_000_000 => 5000,
        >= 2_000_000 => 1536,
        _ => 600,
    };

    private static void Check(ErrT err, string operation)
    {
        if (err != ErrT.Success)
        {
            throw new InvalidOperationException($"sdrplay_api_{operation} failed: {err}");
        }
    }
}
