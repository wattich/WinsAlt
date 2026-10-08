using System.Text.Json.Serialization;
using WinsAlt.Infrastructure;

namespace WinsAlt.Web;

// ---------------------------------------------------------------------------
// JSON source-generation for Native AOT (no reflection-based serialization).
// Every type that crosses a JsonSerializer call must be registered here.
// ---------------------------------------------------------------------------

// ----- API request/response DTOs (named records - anonymous types aren't AOT-safe) -----
// StartedAt (unix seconds) identifies this run of the process: the page uses it to see that a restart has happened.
/// <summary>Public (no sign-in). Listener = the NBNS listener's state only ("Active", "Error", ...) - the installer's
/// final check reads it here because /api/status needs a sign-in by default.</summary>
public record InfoResponse(string Version, string Name, long StartedAt, string Listener = "");
public record ErrorResponse(string Error);
/// <summary>
/// Who is looking at the dashboard. Admin = may change things (signed in, or presenting the API
/// token); everyone else only looks. DefaultPassword = the signed-in admin is still on admin / admin  - 
/// told only to an admin, never to an anonymous visitor (it would advertise working credentials).
/// CanView false = "Sign-in required to view" is on and this browser is not signed in: the page asks for a sign-in
/// instead of polling data endpoints that would only answer 401.
/// </summary>
public record MeResponse(bool Admin, bool SignedIn, string Username, bool DefaultPassword = false, bool CanView = true);
public record LoginRequest(string? Username, string? Password);
public record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public record ListenerDto(string State, string EndPoint, string? LastError, long StartedAt, int Workers,
    bool AnswerBroadcasts, string ConflictPolicy);
public record DatabaseDto(int Total, int Static, int Dynamic, int Replicas);
public record ReplicationKeyRequest(string? Key);
public record CacheClearResponse(int Cleared);
public record CountersDto(long PacketsReceived, long ResponsesSent, long Queries, long QueryHits, long QueryMisses,
    long DnsHits, long PartnerHits, long ReplicaHits, long Registrations, long Refreshes, long Releases, long Conflicts, long RefusedByPolicy, long BlockedNames, long RateLimited, long Oversized, long Expired, long Malformed,
    long Dropped, long IgnoredBroadcasts, long Unsupported);
public record DnsDto(bool Enabled, IReadOnlyList<string> Servers, string Suffix);
public record SelfDto(bool Enabled, string Name, IReadOnlyList<string> Addresses, string Group, IReadOnlyList<string> Suffixes);
/// <summary>The security policy in effect, for the Overview page.</summary>
public record SecurityDto(string RegistrationMode, IReadOnlyList<string> AllowedSubnets, bool RequireAddressMatchesSource,
    IReadOnlyList<string> BlockedNames, int MaxDynamicNames, int MaxNamesPerAddress, int RateLimitPerSecond, int RateLimitBurst,
    bool RequireSignInToView);
public record StatusResponse(ListenerDto Listener, DatabaseDto Database, CountersDto Counters, DnsDto Dns,
    SelfDto Self, SecurityDto Security, MetricsService.Snapshot Metrics);
public record NetbtResponse(bool Supported, IReadOnlyList<NetbtAdapter> Adapters);

/// <summary>
/// One setting on the Settings page. Values travel as text whatever their type (bool = "true" /
/// "false", list = comma separated). Value is what is saved (what the form edits), Running what the
/// service is using now - they differ while a change waits for a restart. A secret is never sent:
/// IsSet says whether one is stored.
/// </summary>
public record SettingDto(string Key, string Section, string Label, string Type, string Value, string Running, string Default,
    int? Min, int? Max, IReadOnlyList<string>? Options, string? Unit, bool RestartRequired, bool Pending, bool? IsSet);
/// <summary>CanRestart: the process runs as a service (ServiceManager: "Windows service" / "systemd"), so the dashboard can restart it.</summary>
public record SettingsResponse(IReadOnlyList<SettingDto> Items, IReadOnlyList<string> PendingRestart, string File, bool CanRestart, string ServiceManager);
/// <summary>Firewall: "Windows Firewall", "firewalld", "ufw" or "none" (no active firewall manager found - Supported is then false).</summary>
public record FirewallResponse(bool Supported, IReadOnlyList<FirewallRule> Rules, string? Error = null, string Firewall = "none");

/// <summary>
/// One name the server can answer for. Source says where it lives: "Local" (registered or static
/// on this server), "Replication" (copied from another WinsAlt server) or "Partner" (an answer
/// forwarded from a partner WINS server, held in the short-lived cache); Origin names that server.
/// ExpiresAt is unix seconds; 0 = static (never expires).
/// </summary>
public record NameDto(string Id, string Name, string Suffix, string Role, string Kind, string NodeType, bool IsStatic,
    IReadOnlyList<string> Addresses, long RegisteredAt, long ExpiresAt, string? Comment, string? Origin = null, string Source = "Local");
public record NamesResponse(int Total, int Matched, IReadOnlyList<NameDto> Items);
public record ResolveResponse(string Name, string Suffix, bool Found, string Source, IReadOnlyList<string> Addresses);

/// <summary>HTTP API serialization (camelCase, compact, omit nulls).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(InfoResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(MeResponse))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(ChangePasswordRequest))]
[JsonSerializable(typeof(StatusResponse))]
[JsonSerializable(typeof(NamesResponse))]
[JsonSerializable(typeof(ResolveResponse))]
[JsonSerializable(typeof(NetbtResponse))]
[JsonSerializable(typeof(PartnerServer))]
[JsonSerializable(typeof(ReplicationStatus))]
[JsonSerializable(typeof(ReplicationPeer))]
[JsonSerializable(typeof(ReplicationKeyRequest))]
[JsonSerializable(typeof(CacheClearResponse))]
[JsonSerializable(typeof(SettingsResponse))]
[JsonSerializable(typeof(FirewallResponse))]
[JsonSerializable(typeof(IReadOnlyList<LanguageInfo>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(IReadOnlyList<PartnerDto>))]
[JsonSerializable(typeof(StaticMapping))]
[JsonSerializable(typeof(IReadOnlyList<StaticMapping>))]
[JsonSerializable(typeof(MetricsService.Snapshot))]
[JsonSerializable(typeof(IReadOnlyList<MetricsService.HistoryPoint>))]
[JsonSerializable(typeof(IReadOnlyList<InMemoryLogStore.Entry>))]
[JsonSerializable(typeof(IReadOnlyList<QueryLogDto>))]
internal partial class ApiJsonContext : JsonSerializerContext { }

/// <summary>static-mappings.json (camelCase, indented for hand-editing).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true)]
[JsonSerializable(typeof(StaticMappingFileModel))]
[JsonSerializable(typeof(PartnerFileModel))]
[JsonSerializable(typeof(ReplicationFileModel))]
[JsonSerializable(typeof(AuthCreds))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class ConfigJsonContext : JsonSerializerContext { }

/// <summary>wins-db.json - the dynamic registration snapshot (camelCase, compact).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DbFileModel))]
internal partial class DbJsonContext : JsonSerializerContext { }

/// <summary>Replication link between WinsAlt servers (camelCase, compact).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ReplRequest))]
[JsonSerializable(typeof(ReplResponse))]
internal partial class ReplJsonContext : JsonSerializerContext { }
