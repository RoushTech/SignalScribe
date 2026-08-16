using SignalScribe.Capture.Dsp;
using SignalScribe.Capture.HostApi;
using SignalScribe.Capture.Settings;
using SignalScribe.Capture.Sources;
using SignalScribe.Contracts;

namespace SignalScribe.Capture;

/// <summary>
/// Pipeline host: source → wideband spectrum (waterfall) → [milestone 1: PFB channelizer →
/// per-channel squelch/demod → Opus clips + markers] → HostClient. Runs a re-init watchdog loop:
/// any source failure, stall, or settings change tears down and rebuilds.
/// </summary>
public sealed class CaptureService(
    IConfiguration config,
    HostClient hostClient,
    CaptureSettingsProvider settings,
    SdrPlayDeviceEnumerator deviceEnumerator,
    ILogger<CaptureService> logger) : BackgroundService
{
    private static readonly TimeSpan EnumerateInterval = TimeSpan.FromSeconds(30);

    private volatile bool _reinitRequested;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;

        // Operator settings come from the host (DB-backed, UI-edited); appsettings is only the
        // unreachable-host fallback. TODO(milestone 1): split disruptive (freq/rate/spacing/device
        // ⇒ this re-init) from live-apply (gain/AGC/squelch ⇒ sdrplay_api_Update without teardown).
        await settings.RefreshAsync(stoppingToken);
        settings.Changed += _ => _reinitRequested = true;

        var replayed = await hostClient.ReplaySpoolAsync(stoppingToken);
        if (replayed > 0)
        {
            logger.LogInformation("Replayed {Count} spooled events", replayed);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunPipelineAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError("Capture pipeline failed: {Message} — retrying in 10s", ex.Message);
                StatusReporter.Snapshot = StatusReporter.Snapshot with { State = "degraded", Source = ex.Message };
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    private async Task RunPipelineAsync(CancellationToken ct)
    {
        _reinitRequested = false;
        var devices = deviceEnumerator.Enumerate();

        using var source = CreateSource();
        source.Start();
        var anchorUtc = DateTime.UtcNow; // all transmission timestamps derive from sample counts off this anchor

        var activeSerial = (source as SdrPlaySource)?.ActiveSerial;
        if (activeSerial is not null)
        {
            devices = deviceEnumerator.Enumerate(activeSerial);
        }

        // Signal-time per waterfall row (seconds) — sets scroll speed and averaging depth together.
        const double secondsPerRow = 0.1;
        var analyzer = new SpectrumAnalyzer(
            fftSize: 1024,
            averageFrames: Math.Max(4, (int)(source.SampleRate * secondsPerRow / 1024)));

        var s = settings.Current;
        var channelizer = new PolyphaseChannelizer(source.SampleRate, s?.ChannelSpacingHz ?? 12_500);
        var knownFrequencies = await hostClient.GetKnownFrequenciesAsync(ct) ?? [];
        var bank = new ChannelBank(
            channelizer.ChannelCount,
            channelizer.OutputSampleRate,
            source.CenterFrequencyHz,
            bin => channelizer.BinFrequencyHz(bin, source.CenterFrequencyHz),
            s?.SquelchOpenDb ?? 8,
            s?.SquelchCloseDb ?? 5,
            s?.SquelchHangMs ?? 400,
            config.GetValue("Capture:AudioDirectory", "audio")!,
            anchorUtc,
            freq => knownFrequencies.Contains(freq),
            ingest => _ = hostClient.PostTransmissionAsync(ingest, CancellationToken.None),
            logger,
            s?.DeviationHz ?? NbfmDemodulator.DefaultDeviationHz,
            s?.MonitorLowHz ?? 144_000_000,
            s?.MonitorHighHz ?? 148_000_000,
            discard => _ = hostClient.PostDiscardAsync(discard, CancellationToken.None));

        // Front-end overload is ground truth from the hardware: while the ADC is clipping, every bin
        // carries compression products rather than signal. Feeding it to the bank holds every gate
        // shut for the duration instead of recording a band-wide burst of nothing.
        var overloadSource = source as SdrPlaySource;

        var buffer = new float[65536];
        var lastStatus = DateTime.UtcNow;
        var lastEnumerate = DateTime.UtcNow;
        var lastKnownRefresh = DateTime.UtcNow;
        long lastHopIndex = 0;

        StatusReporter.Snapshot = new CaptureStatusSnapshot(
            "running", Describe(source), source.SampleCounter, 0, 0, devices, []);
        logger.LogInformation(
            "Pipeline running: {Channels} channels @ {Spacing} Hz spacing, {Known} known frequencies",
            channelizer.ChannelCount, channelizer.ChannelSpacingHz, knownFrequencies.Count);

        var hopTracker = new HopTrackingSink(bank);

        try
        {
            while (!ct.IsCancellationRequested && !_reinitRequested)
            {
                var n = source.Read(buffer);
                if (n == 0)
                {
                    logger.LogInformation("Source ended — restarting pipeline (IQ fixtures loop)");
                    return;
                }

                if (n < 0)
                {
                    // Stalled sample counter is the sdrplay-service-wedged signal: tear down, re-init.
                    logger.LogWarning("Sample stream stalled — tearing down and re-initializing");
                    return;
                }

                if (overloadSource is not null)
                {
                    bank.Overloaded = overloadSource.Overloaded;
                }

                var span = buffer.AsSpan(0, n);
                var row = analyzer.Feed(span, source.CenterFrequencyHz, (long)source.SampleRate);
                if (row is not null)
                {
                    SpectrumPublisher.Publish(row);
                }

                channelizer.Process(span, hopTracker);
                lastHopIndex = hopTracker.LastHopIndex;

                var now = DateTime.UtcNow;
                if (now - lastKnownRefresh > TimeSpan.FromSeconds(30))
                {
                    lastKnownRefresh = now;
                    var refreshed = await hostClient.GetKnownFrequenciesAsync(ct);
                    if (refreshed is not null)
                    {
                        knownFrequencies.Clear();
                        knownFrequencies.UnionWith(refreshed);
                    }
                }

                if (now - lastEnumerate > EnumerateInterval)
                {
                    lastEnumerate = now;
                    devices = deviceEnumerator.Enumerate(activeSerial);
                }

                if (now - lastStatus > TimeSpan.FromSeconds(1))
                {
                    lastStatus = now;
                    StatusReporter.Snapshot = new CaptureStatusSnapshot(
                        "running",
                        Describe(source),
                        source.SampleCounter,
                        bank.OpenGateCount,
                        (source as SdrPlaySource)?.AdcOverloads ?? 0,
                        devices,
                        bank.ActiveGates(lastHopIndex));
                }
            }
        }
        finally
        {
            // Teardown closes clips gracefully and posts them — never drops open recordings.
            bank.CloseAll(lastHopIndex);
        }
    }

    private sealed class HopTrackingSink(ChannelBank bank) : IChannelizerSink
    {
        public long LastHopIndex { get; private set; }

        public void OnHop(ReadOnlySpan<float> channelsInterleaved, long hopIndex)
        {
            LastHopIndex = hopIndex;
            bank.OnHop(channelsInterleaved, hopIndex);
        }
    }

    private ISampleSource CreateSource()
    {
        if (config.GetValue<string>("Capture:Source") == "IqFile")
        {
            // Replay is deployment config: rate/center must match the fixture file, not operator settings.
            return new IqFileSource(
                config.GetValue<string>("Capture:IqFilePath")
                    ?? throw new InvalidOperationException("Capture:IqFilePath is required for the IqFile source"),
                config.GetValue("Capture:SampleRate", 1_000_000.0),
                config.GetValue("Capture:CenterFrequencyHz", 146_400_000L),
                throttle: config.GetValue("Capture:IqFileThrottle", true));
        }

        var s = settings.Current;
        return new SdrPlaySource(
            s?.SampleRateHz ?? config.GetValue("Capture:SampleRate", 6_400_000L),
            s?.CenterFrequencyHz ?? config.GetValue("Capture:CenterFrequencyHz", 146_400_000L),
            s?.DeviceSerial,
            s?.GainReductionDb ?? 40,
            s?.LnaState ?? 0,
            s?.AgcEnabled ?? true,
            logger);
    }

    private static string Describe(ISampleSource source) => source is SdrPlaySource sdr
        ? $"{sdr.ActiveModel} {sdr.ActiveSerial}"
        : source.GetType().Name;
}
