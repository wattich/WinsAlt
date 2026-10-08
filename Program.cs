using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Hosting.WindowsServices;
using WinsAlt;
using WinsAlt.Core.Domain;
using WinsAlt.Infrastructure;
using WinsAlt.Network;
using WinsAlt.Web;

// One-shot helper commands used by the installer (--pick-port, --listener-state): answer and exit.
if (InstallerCommands.TryRun(args) is { } exitCode) return exitCode;
if (BenchCommand.TryRun(args) is { } benchExit) return benchExit;
// The helper a running service starts to have itself restarted (dashboard: Settings > Restart service).
if (ServiceControl.TryRun(args) is { } restartExit) return restartExit;

// CreateSlimBuilder: the AOT-friendly host (no reflection-based defaults).
var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
{
    Args = args,
    // ContentRoot is always the exe folder so appsettings.json is found when running as a Windows Service.
    ContentRootPath = AppContext.BaseDirectory
});

// The dashboard only ever receives small JSON bodies: cap what a request may make the server buffer.
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = 64 * 1024;
    kestrel.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
    kestrel.Limits.MaxConcurrentConnections = 200;
});

// Settings saved from the dashboard (settings.json in the data folder) go on top of appsettings.json
// but below environment variables and command-line arguments, which keep the last word.
string dataDirectory = builder.Configuration["Wins:DataDirectory"] is { Length: > 0 } configuredDirectory
    ? Path.GetFullPath(configuredDirectory, AppContext.BaseDirectory)
    : AppContext.BaseDirectory;
var settingsOverlay = new SettingsOverlay(Path.Combine(dataDirectory, SettingsOverlay.FileName));
var configSources = builder.Configuration.Sources;
int overlayIndex = configSources.Count;
for (int i = configSources.Count - 1; i >= 0; i--)
    if (configSources[i] is EnvironmentVariablesConfigurationSource { Prefix: null or "" }) { overlayIndex = i; break; }
configSources.Insert(overlayIndex, settingsOverlay);
builder.Services.AddSingleton(settingsOverlay);

// "Open the dashboard to other computers" (Settings page) decides which address Kestrel listens on;
// the port stays the one in appsettings.json.
if (WinsOptions.DashboardUrlOverride(builder.Configuration) is { } dashboardUrl)
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [WinsOptions.DashboardUrlKey] = dashboardUrl });

// Run as a Windows Service + auto-configure EventLog logging when running as a service.
// Started from a terminal this is a no-op and the same binary runs as an interactive console app.
builder.Host.UseWindowsService(o => o.ServiceName = ServiceControl.ServiceName);
// The same on Linux under systemd (no-op anywhere else): readiness / stop notifications, journald log format.
builder.Host.UseSystemd();

// AOT JSON: resolve all API types via the source-generated context (no reflection).
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default);
    // The context's own [JsonSourceGenerationOptions] do not apply to these HTTP options.
    o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});

// In-memory log buffer for the dashboard Events view (captures all providers' records).
var logStore = new InMemoryLogStore();
builder.Services.AddSingleton(logStore);
builder.Logging.AddProvider(new InMemoryLoggerProvider(logStore));

// ----- Core / infrastructure -----
builder.Services.AddSingleton(WinsOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<WinsCounters>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<NameStore>();
builder.Services.AddSingleton<WinsDatabase>();
builder.Services.AddSingleton<StaticMappingService>();
builder.Services.AddSingleton<DnsFallbackResolver>();
builder.Services.AddSingleton<PartnerService>();
builder.Services.AddSingleton<ReplicaStore>();
builder.Services.AddSingleton<ReplicationService>();
builder.Services.AddSingleton<QueryLog>();
builder.Services.AddSingleton<MetricsService>();
builder.Services.AddSingleton<NetbtAdapterService>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<FirewallService>();
builder.Services.AddSingleton<LanguageService>();

// ----- Network engine -----
builder.Services.AddSingleton<NameChallengeService>();
builder.Services.AddSingleton<NbnsRequestHandler>();
builder.Services.AddSingleton<NbnsServer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NbnsServer>());
builder.Services.AddHostedService<NameMaintenanceService>();
builder.Services.AddHostedService<ReplicationServer>();
builder.Services.AddHostedService<ReplicationClient>();
builder.Services.AddSingleton<SelfRegistrationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SelfRegistrationService>());

// ----- CLI interactive mode (development / debugging) -----
// Only when a person is at the keyboard: not as a service, and not with stdin piped or detached.
if (!ServiceControl.RunningAsService && !Console.IsInputRedirected)
    builder.Services.AddHostedService<ConsoleCommandService>();

var app = builder.Build();

// Fill the name database before the listener accepts its first packet: saved registrations
// first, then static mappings on top (a static mapping overrides a dynamic record of that name).
app.Services.GetRequiredService<WinsDatabase>();
app.Services.GetRequiredService<StaticMappingService>();
app.Services.GetRequiredService<MetricsService>();
app.Services.GetRequiredService<SettingsService>(); // reports an unreadable settings.json at startup, not at first use

app.UseWinsSecurity();
app.MapWinsApi();

app.Run();
// Non-zero only when the dashboard asked for a restart under systemd (ServiceControl.RestartExitCode).
return Environment.ExitCode;
