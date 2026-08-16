using SignalScribe.Capture;
using SignalScribe.Capture.HostApi;
using SignalScribe.Capture.Settings;
using SignalScribe.Capture.Sources;
using SignalScribe.Capture.Spool;

var builder = Host.CreateApplicationBuilder(args);
var services = builder.Services;
var config = builder.Configuration;

var hostBaseUrl = new Uri(config.GetValue("Host:BaseUrl", "http://localhost:5020")!);

services
    .AddSingleton(new EventSpool(config.GetValue("Capture:SpoolDirectory", "spool")!))
    .AddSingleton<CaptureSettingsProvider>()
    .AddSingleton<SdrPlayDeviceEnumerator>()
    .AddHttpClient<HostClient>(client =>
        {
            client.BaseAddress = hostBaseUrl;
            client.Timeout = TimeSpan.FromSeconds(10);
        })
        .AddTypedClient((http, sp) => new HostClient(
            http,
            sp.GetRequiredService<EventSpool>(),
            config.GetValue("Capture:AudioDirectory", "audio")!,
            sp.GetRequiredService<ILogger<HostClient>>()))
        .Services
    .AddHttpClient(nameof(CaptureSettingsProvider), client =>
        {
            client.BaseAddress = hostBaseUrl;
            client.Timeout = TimeSpan.FromSeconds(10);
        })
        .Services
    .AddHostedService<CaptureService>()
    .AddHostedService<StatusReporter>();

builder.Build().Run();
