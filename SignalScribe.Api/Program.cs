using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SignalScribe.Api.Hubs;
using SignalScribe.Api.Services;
using SignalScribe.Data;

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;
var config = builder.Configuration;

services
    .AddSingleton<ServiceStatusCache>()
    .AddSingleton<SpectrumCache>()
    .AddDbContext<SignalScribeContext>(options => options
        .UseSqlite(config.GetConnectionString("SignalScribe")))
    .AddHostedService<SessionizationService>()
    .AddSingleton<DiscardPurgeService>()
    .AddHostedService(sp => sp.GetRequiredService<DiscardPurgeService>())
    .AddHostedService<ChannelAuditService>()
    .AddSignalR()
        .Services
    .AddControllers()
        .Services
    .AddOpenApi();

var app = builder.Build();

// SQLite won't create parent directories for the database file.
var dataSource = new SqliteConnectionStringBuilder(config.GetConnectionString("SignalScribe")).DataSource;
if (Path.GetDirectoryName(dataSource) is { Length: > 0 } dataDirectory)
{
    Directory.CreateDirectory(dataDirectory);
}

// Single writer: this process is the only one that ever touches the database.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SignalScribeContext>();
    db.Database.Migrate();
    db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
}

app.UseDefaultFiles()
    .UseStaticFiles();

app.MapControllers();
app.MapHub<StatusHub>("/hubs/status");
app.MapOpenApi();
app.MapFallbackToFile("index.html");

app.Run();
