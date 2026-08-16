namespace SignalScribe.Contracts;

/// <summary>SignalR event names on /hubs/status (the one socket daemons and browsers share).</summary>
public static class HubEvents
{
    public const string StatusChanged = "statusChanged";

    public const string SettingsChanged = "settingsChanged";

    public const string Spectrum = "spectrum";

    public const string TransmissionChanged = "transmissionChanged";
}

/// <summary>
/// Operator-tunable radio configuration — stored in the DB, edited via api/v0/settings, pushed to
/// the capture daemon over the status hub. Deployment config (paths, URLs) stays in appsettings/env.
/// </summary>
public record CaptureSettingsDto(
    long CenterFrequencyHz,
    long SampleRateHz,
    int ChannelSpacingHz,
    int GainReductionDb,
    int LnaState,
    bool AgcEnabled,
    double SquelchOpenDb,
    double SquelchCloseDb,
    int SquelchHangMs,
    double DeviationHz,
    long MonitorLowHz,
    long MonitorHighHz,
    string? DeviceSerial);

public record WorkerSettingsDto(
    string WhisperModel,
    string TranscriptionPrompt,
    string SummaryModel,
    int MaxJobsPerClaim,
    int TranscriptionThreads,
    int SummaryThreads,
    bool Paused,
    int DiscardRetentionHours);

/// <summary>Model files actually present in the models directory, for the settings dropdowns.</summary>
public record AvailableModelsDto(List<string> Whisper, List<string> Summary);
