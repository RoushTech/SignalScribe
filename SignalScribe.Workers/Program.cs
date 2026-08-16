using SignalScribe.Workers;
using SignalScribe.Workers.Handlers;
using SignalScribe.Workers.HostApi;
using SignalScribe.Workers.Settings;

// On CPUs without AVX (e.g. QEMU default vCPUs), LLamaSharp's runtime auto-probe can SIGILL the
// whole process before managed code sees anything — select the noavx native build explicitly.
if (!System.Runtime.Intrinsics.X86.Avx.IsSupported)
{
    LLama.Native.NativeLibraryConfig.All.WithAvx(LLama.Native.AvxLevel.None);
}

var builder = Host.CreateApplicationBuilder(args);
var services = builder.Services;
var config = builder.Configuration;

var hostBaseUrl = new Uri(config.GetValue("Host:BaseUrl", "http://localhost:5020")!);

services
    .AddSingleton<WorkerSettingsProvider>()
    .AddSingleton<IJobHandler, TranscriptionHandler>()
    .AddSingleton<IJobHandler, EmbeddingHandler>()
    .AddSingleton<IJobHandler, SummaryHandler>()
    .AddHttpClient<JobsClient>(client =>
        {
            client.BaseAddress = hostBaseUrl;
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .Services
    .AddHttpClient<InternalApiClient>(client =>
        {
            client.BaseAddress = hostBaseUrl;
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .Services
    .AddHttpClient(nameof(WorkerSettingsProvider), client =>
        {
            client.BaseAddress = hostBaseUrl;
            client.Timeout = TimeSpan.FromSeconds(10);
        })
        .Services
    .AddHostedService<JobPollerService>()
    .AddHostedService<StatusReporter>();

builder.Build().Run();
