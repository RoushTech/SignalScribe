using System.Runtime.InteropServices;

namespace SignalScribe.Capture.Sources;

/// <summary>
/// P/Invoke bindings for sdrplay_api v3.15 (layouts taken verbatim from the vendored headers in
/// vendor/sdrplay/inc — do not modify without re-checking them). All structs are accessed either
/// by value (DeviceT) or through API-owned memory via pointers (DeviceParamsT tree), so the
/// model-specific tails of DevParamsT/RxChannelParamsT are deliberately omitted: every field we
/// touch precedes them, and we never allocate those structs ourselves.
/// </summary>
internal static unsafe class SdrPlayInterop
{
    // The SONAME — this is what ldconfig registers; the full .3.15 name is not in the loader cache.
    private const string Lib = "libsdrplay_api.so.3";

    public const int MaxDevices = 16;

    public const int MaxSerNoLen = 64;

    // --- enums (all int-sized in C) ---

    public enum ErrT
    {
        Success = 0,
        Fail = 1,
        InvalidParam = 2,
        OutOfRange = 3,
        GainUpdateError = 4,
        RfUpdateError = 5,
        FsUpdateError = 6,
        HwError = 7,
        AliasingError = 8,
        AlreadyInitialised = 9,
        NotInitialised = 10,
        NotEnabled = 11,
        HwVerError = 12,
        OutOfMemError = 13,
        ServiceNotResponding = 14,
    }

    public enum TunerSelect
    {
        Neither = 0,
        A = 1,
        B = 2,
        Both = 3,
    }

    [Flags]
    public enum ReasonForUpdate : uint
    {
        None = 0,
        Dev_Fs = 0x00000001,
        Tuner_Gr = 0x00008000,
        Tuner_Frf = 0x00020000,
        Tuner_BwType = 0x00040000,
        Tuner_IfType = 0x00080000,
        Ctrl_Agc = 0x01000000,
        Ctrl_OverloadMsgAck = 0x04000000,
    }

    /// <summary>
    /// sdrplay_api_PowerOverloadCbEventIdT — the first field of the event-params union, so it can be
    /// read straight off the pointer. Distinguishes the front end going into overload from coming out.
    /// </summary>
    public enum OverloadChange
    {
        Detected = 0,
        Corrected = 1,
    }

    public enum SdrEvent
    {
        GainChange = 0,
        PowerOverloadChange = 1,
        DeviceRemoved = 2,
        RspDuoModeChange = 3,
        DeviceFailure = 4,
    }

    // --- structs ---

    /// <summary>sdrplay_api_DeviceT — 96 bytes on x86-64 (SerNo@0, hwVer@64, tuner@68, rspDuoMode@72, valid@76, rspDuoSampleFreq@80, dev@88).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceT
    {
        public fixed byte SerNo[MaxSerNoLen];
        public byte HwVer;
        public int Tuner;
        public int RspDuoMode;
        public byte Valid;
        public double RspDuoSampleFreq;
        public IntPtr Dev;

        public readonly string Serial
        {
            get
            {
                fixed (byte* p = SerNo)
                {
                    return Marshal.PtrToStringAnsi((IntPtr)p) ?? string.Empty;
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceParamsT
    {
        public DevParamsT* DevParams;
        public RxChannelParamsT* RxChannelA;
        public RxChannelParamsT* RxChannelB;
    }

    // Truncated after SamplesPerPkt (the model-specific param tails follow in C; never allocated here).
    [StructLayout(LayoutKind.Sequential)]
    public struct DevParamsT
    {
        public double Ppm;
        public FsFreqT FsFreq;
        public SyncUpdateT SyncUpdate;
        public ResetFlagsT ResetFlags;
        public int Mode;                 // sdrplay_api_TransferModeT
        public uint SamplesPerPkt;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FsFreqT
    {
        public double FsHz;
        public byte SyncUpdate;
        public byte ReCal;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SyncUpdateT
    {
        public uint SampleNum;
        public uint Period;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ResetFlagsT
    {
        public byte ResetGainUpdate;
        public byte ResetRfUpdate;
        public byte ResetFsUpdate;
    }

    // Truncated after CtrlParams (rsp1a/rsp2/rspDuo/rspDx tuner param tails follow in C).
    [StructLayout(LayoutKind.Sequential)]
    public struct RxChannelParamsT
    {
        public TunerParamsT TunerParams;
        public ControlParamsT CtrlParams;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TunerParamsT
    {
        public int BwType;               // sdrplay_api_Bw_MHzT (kHz value: 200..8000)
        public int IfType;               // sdrplay_api_If_kHzT (0 = zero-IF)
        public int LoMode;               // sdrplay_api_LoModeT (1 = auto)
        public GainT Gain;
        public RfFreqT RfFreq;
        public DcOffsetTunerT DcOffsetTuner;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GainT
    {
        public int GRdB;                 // 20..59
        public byte LnaState;
        public byte SyncUpdate;
        public int MinGr;                // sdrplay_api_MinGainReductionT (20 = normal)
        public GainValuesT GainVals;     // output
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GainValuesT
    {
        public float Curr;
        public float Max;
        public float Min;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RfFreqT
    {
        public double RfHz;
        public byte SyncUpdate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DcOffsetTunerT
    {
        public byte DcCal;
        public byte SpeedUp;
        public int TrackTime;
        public int RefreshRateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ControlParamsT
    {
        public DcOffsetT DcOffset;
        public DecimationT Decimation;
        public AgcT Agc;
        public int AdsbMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DcOffsetT
    {
        public byte DCenable;
        public byte IQenable;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DecimationT
    {
        public byte Enable;
        public byte DecimationFactor;
        public byte WideBandSignal;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AgcT
    {
        public int Enable;               // sdrplay_api_AgcControlT (0 = off, 2 = 50 Hz)
        public int SetPointDbfs;
        public ushort AttackMs;
        public ushort DecayMs;
        public ushort DecayDelayMs;
        public ushort DecayThresholdDb;
        public int SyncUpdate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StreamCbParamsT
    {
        public uint FirstSampleNum;
        public int GrChanged;
        public int RfChanged;
        public int FsChanged;
        public uint NumSamples;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CallbackFnsT
    {
        public IntPtr StreamACbFn;
        public IntPtr StreamBCbFn;
        public IntPtr EventCbFn;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void StreamCallback(short* xi, short* xq, StreamCbParamsT* cbParams, uint numSamples, uint reset, IntPtr cbContext);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void EventCallback(SdrEvent eventId, TunerSelect tuner, IntPtr eventParams, IntPtr cbContext);

    // --- functions ---

    [DllImport(Lib, EntryPoint = "sdrplay_api_Open")]
    public static extern ErrT Open();

    [DllImport(Lib, EntryPoint = "sdrplay_api_Close")]
    public static extern ErrT Close();

    [DllImport(Lib, EntryPoint = "sdrplay_api_ApiVersion")]
    public static extern ErrT ApiVersion(out float version);

    [DllImport(Lib, EntryPoint = "sdrplay_api_LockDeviceApi")]
    public static extern ErrT LockDeviceApi();

    [DllImport(Lib, EntryPoint = "sdrplay_api_UnlockDeviceApi")]
    public static extern ErrT UnlockDeviceApi();

    [DllImport(Lib, EntryPoint = "sdrplay_api_GetDevices")]
    public static extern ErrT GetDevices(DeviceT* devices, out uint numDevs, uint maxDevs);

    [DllImport(Lib, EntryPoint = "sdrplay_api_SelectDevice")]
    public static extern ErrT SelectDevice(ref DeviceT device);

    [DllImport(Lib, EntryPoint = "sdrplay_api_ReleaseDevice")]
    public static extern ErrT ReleaseDevice(ref DeviceT device);

    [DllImport(Lib, EntryPoint = "sdrplay_api_GetDeviceParams")]
    public static extern ErrT GetDeviceParams(IntPtr dev, out DeviceParamsT* deviceParams);

    [DllImport(Lib, EntryPoint = "sdrplay_api_Init")]
    public static extern ErrT Init(IntPtr dev, ref CallbackFnsT callbackFns, IntPtr cbContext);

    [DllImport(Lib, EntryPoint = "sdrplay_api_Uninit")]
    public static extern ErrT Uninit(IntPtr dev);

    [DllImport(Lib, EntryPoint = "sdrplay_api_Update")]
    public static extern ErrT Update(IntPtr dev, TunerSelect tuner, ReasonForUpdate reason, uint reasonExt1);

    public static string ModelName(byte hwVer) => hwVer switch
    {
        1 => "RSP1",
        2 => "RSP2",
        3 => "RSPduo",
        4 => "RSPdx",
        6 => "RSP1B",
        7 => "RSPdx-R2",
        255 => "RSP1A",
        _ => $"RSP (hwVer {hwVer})",
    };
}
