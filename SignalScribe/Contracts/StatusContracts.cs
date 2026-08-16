namespace SignalScribe.Contracts;

/// <summary>
/// Streaming status pushed by capture/workers over the /hubs/status SignalR hub, relayed live
/// to browser clients. In-memory only — never persisted (daemons re-push on reconnect).
/// </summary>
public record ServiceStatusUpdate(
    string Service,
    string State,
    DateTime TimestampUtc,
    Dictionary<string, string> Details,
    List<SdrDeviceInfo>? Devices = null,
    List<ActiveGate>? Gates = null);

/// <summary>A channel currently being recorded — surfaced live so the operator can see what capture is doing.</summary>
public record ActiveGate(long FrequencyHz, double Seconds, double PeakDbfs, bool Known);

/// <summary>An attached SDR enumerated by the capture daemon. Serial is the stable identity (survives USB re-enumeration).</summary>
public record SdrDeviceInfo(string Serial, string Model, bool InUse);

/// <summary>
/// One waterfall row: FFT-averaged wideband power, quantized to bytes over [MinDb, MaxDb].
/// Pushed by the capture daemon over the status hub (~10 Hz), relayed live to browsers.
/// </summary>
public record SpectrumRow(
    long CenterFrequencyHz,
    long SpanHz,
    DateTime TimestampUtc,
    double MinDb,
    double MaxDb,
    byte[] Bins);

public static class ServiceNames
{
    public const string Capture = "capture";

    public const string Workers = "workers";
}

public static class ServiceStates
{
    public const string Running = "running";

    public const string Degraded = "degraded";

    public const string Idle = "idle";

    public const string Offline = "offline";
}
