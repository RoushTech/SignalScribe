using Microsoft.Extensions.Logging;
using SignalScribe.Contracts;

namespace SignalScribe.Capture.Sources;

/// <summary>
/// Enumerates attached SDRPlay devices via sdrplay_api. The list rides the status stream so the
/// Settings UI offers device selection; the chosen serial comes back via CaptureSettings.DeviceSerial.
/// Degrades to an empty list (with one-time warnings) when the API library or service is absent,
/// so the daemon runs fine on hosts without the SDRplay stack (e.g. IQ-replay development).
/// </summary>
public sealed class SdrPlayDeviceEnumerator(ILogger<SdrPlayDeviceEnumerator> logger)
{
    private static readonly Lock ApiLock = new();

    private static bool _apiOpened;

    private bool _warnedLibraryMissing;

    private bool _warnedServiceDown;

    public unsafe IReadOnlyList<SdrDeviceInfo> Enumerate(string? activeSerial = null)
    {
        lock (ApiLock)
        {
            try
            {
                if (!_apiOpened)
                {
                    var openErr = SdrPlayInterop.Open();
                    if (openErr != SdrPlayInterop.ErrT.Success)
                    {
                        WarnServiceDown(openErr);
                        return [];
                    }

                    _apiOpened = true;
                    SdrPlayInterop.ApiVersion(out var version);
                    logger.LogInformation("sdrplay_api opened, service version {Version:F2}", version);
                }

                if (SdrPlayInterop.LockDeviceApi() != SdrPlayInterop.ErrT.Success)
                {
                    return [];
                }

                try
                {
                    var devices = stackalloc SdrPlayInterop.DeviceT[SdrPlayInterop.MaxDevices];
                    var err = SdrPlayInterop.GetDevices(devices, out var count, SdrPlayInterop.MaxDevices);
                    if (err != SdrPlayInterop.ErrT.Success)
                    {
                        WarnServiceDown(err);
                        return [];
                    }

                    _warnedServiceDown = false;
                    var result = new List<SdrDeviceInfo>((int)count);
                    for (var i = 0; i < count; i++)
                    {
                        var serial = devices[i].Serial;
                        result.Add(new SdrDeviceInfo(
                            serial,
                            SdrPlayInterop.ModelName(devices[i].HwVer),
                            InUse: serial == activeSerial || devices[i].Valid == 0));
                    }

                    return result;
                }
                finally
                {
                    SdrPlayInterop.UnlockDeviceApi();
                }
            }
            catch (DllNotFoundException)
            {
                if (!_warnedLibraryMissing)
                {
                    _warnedLibraryMissing = true;
                    logger.LogWarning("libsdrplay_api not found — device enumeration disabled (install the SDRplay API, see scripts/fetch-sdrplay-api.sh)");
                }

                return [];
            }
        }
    }

    private void WarnServiceDown(SdrPlayInterop.ErrT err)
    {
        if (!_warnedServiceDown)
        {
            _warnedServiceDown = true;
            logger.LogWarning("sdrplay_api error {Err} — is sdrplay_apiService running?", err);
        }
    }
}
