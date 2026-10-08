using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using WinsAlt.Core.Domain;
using WinsAlt.Web;

namespace WinsAlt.Infrastructure;

/// <summary>
/// The settings saved from the dashboard: a flat "config key → value" map kept in settings.json in
/// the data folder and layered over appsettings.json (and under environment variables) as an
/// ordinary configuration source. appsettings.json itself is never rewritten - it keeps whatever
/// the installer and the administrator put there, and deleting settings.json undoes every change
/// made in the dashboard.
/// </summary>
public sealed class SettingsOverlay : ConfigurationProvider, IConfigurationSource
{
    public const string FileName = "settings.json";

    public SettingsOverlay(string path)
    {
        FilePath = path;
        try
        {
            if (File.Exists(path)
                && JsonSerializer.Deserialize(File.ReadAllText(path), ConfigJsonContext.Default.DictionaryStringString) is { } saved)
                Data = saved.ToDictionary(p => p.Key, p => (string?)p.Value, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // No logger exists this early; SettingsService reports it once the host is up.
            LoadError = ex.Message;
        }
    }

    public string FilePath { get; }
    public string? LoadError { get; }

    public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

    public Dictionary<string, string> Snapshot() =>
        Data.ToDictionary(p => p.Key, p => p.Value ?? "", StringComparer.OrdinalIgnoreCase);

    /// <summary>Swaps the whole map; everything that reads the configuration afterwards sees the new values.</summary>
    public void Replace(Dictionary<string, string> values)
    {
        Data = values.ToDictionary(p => p.Key, p => (string?)p.Value, StringComparer.OrdinalIgnoreCase);
        OnReload();
    }

    /// <summary>Atomic write (rename-replace), so a crash cannot leave half a file behind.</summary>
    public void Write(Dictionary<string, string> values)
    {
        var sorted = new Dictionary<string, string>(values.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase));
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(sorted, ConfigJsonContext.Default.DictionaryStringString));
        File.Move(tmp, FilePath, overwrite: true);
    }
}

/// <summary>
/// Everything the dashboard's Settings page can show and change: the catalogue of settings (label,
/// kind, limits, explanation), validation, saving, and applying.
///
/// A setting is either looked up on every use - then a change works at once
/// (<see cref="WinsOptions.ApplyHot"/>) - or it was turned into a socket, buffer or timer at startup
/// and needs a service restart; the page says which, and lists what is still waiting for one.
/// Only keys in the catalogue can be written, whatever a request contains: the data folder, the
/// dashboard's own address and logging stay in appsettings.json.
/// </summary>
public sealed class SettingsService
{
    private enum Kind { Bool, Int, Choice, Text, List, Secret }

    private sealed record Def(string Key, string Section, string Label, Kind Kind, bool Hot,
        Func<WinsOptions, string> Get, int Min = 0, int Max = 0, string[]? Options = null, string? Unit = null,
        Func<string, string?>? Check = null);

    private const string SecName = "Name server";
    private const string SecNames = "Names and conflicts";
    private const string SecSelf = "This server's own name";
    private const string SecSecurity = "Security";
    private const string SecDns = "DNS fallback";
    private const string SecRepl = "Replication";
    private const string SecDash = "Dashboard";

    private const string TtlKeyMin = "Wins:MinTtlSeconds", TtlKeyMax = "Wins:MaxTtlSeconds";
    private const string AdminTokenKey = "Dashboard:AdminToken";
    public const int MinAdminTokenLength = 16;

    // Labels here are the English fallback. What the dashboard shows (label and the help text behind the "?" button)
    // comes from the language files, wwwroot/lang/<code>.json, keys "setting.<Key>.label" / "setting.<Key>.help".
    private static readonly Def[] Catalogue =
    [
        // ----- Name server
        new("Wins:ListenAddress", SecName, "Listen address", Kind.Text, false,
            o => o.ListenAddress.ToString(), Check: CheckIpv4),
        new("Wins:Port", SecName, "UDP port", Kind.Int, false,
            o => Num(o.Port), 1, 65535),
        new("Wins:AnswerBroadcasts", SecName, "Answer broadcast queries", Kind.Bool, true,
            o => Flag(o.AnswerBroadcasts)),
        new("Wins:QueueCapacity", SecName, "Packet queue size", Kind.Int, false,
            o => Num(o.QueueCapacity), 64, 1_000_000, Unit: "packets"),
        new("Wins:QueryLogCapacity", SecName, "Query log size", Kind.Int, false,
            o => Num(o.QueryLogCapacity), 16, 100_000, Unit: "entries"),

        // ----- Names and conflicts
        new("Wins:ConflictPolicy", SecNames, "Name conflict policy", Kind.Choice, true,
            o => o.ConflictPolicy.ToString(), Options: ["Challenge", "Overwrite", "Reject"]),
        new(TtlKeyMin, SecNames, "Minimum registration lifetime", Kind.Int, true,
            o => Num(o.MinTtlSeconds), 30, 31_536_000, Unit: "seconds"),
        new(TtlKeyMax, SecNames, "Maximum registration lifetime", Kind.Int, true,
            o => Num(o.MaxTtlSeconds), 60, 31_536_000, Unit: "seconds"),
        new("Wins:SweepIntervalSeconds", SecNames, "Expiry sweep interval", Kind.Int, false,
            o => Num(o.SweepIntervalSeconds), 1, 3600, Unit: "seconds"),
        new("Wins:PersistIntervalSeconds", SecNames, "Save-to-disk interval", Kind.Int, false,
            o => Num(o.PersistIntervalSeconds), 1, 3600, Unit: "seconds"),

        // ----- This server's own name
        new("Wins:RegisterSelf", SecSelf, "Publish this server's own name", Kind.Bool, false,
            o => Flag(o.RegisterSelf)),
        new("Wins:SelfName", SecSelf, "Name to publish", Kind.Text, false,
            o => o.SelfName, Check: v => v.Length == 0 ? null : CheckNetbiosName(v)),
        new("Wins:SelfSuffixes", SecSelf, "Suffixes to publish", Kind.List, false,
            o => string.Join(", ", o.SelfSuffixes.Select(s => s.ToString("X2"))), Check: CheckSelfSuffixes),
        new("Wins:SelfGroup", SecSelf, "Workgroup / domain", Kind.Text, false,
            o => o.SelfGroup, Check: v => v.Length == 0 || v == "-" ? null : CheckNetbiosName(v)),

        // ----- Security
        new("Wins:Security:RegistrationMode", SecSecurity, "Who may register names", Kind.Choice, true,
            o => o.RegistrationMode.ToString(), Options: ["Dynamic", "AllowedSubnets", "StaticOnly"]),
        new("Wins:Security:AllowedSubnets", SecSecurity, "Allowed subnets", Kind.List, true,
            o => string.Join(", ", o.AllowedSubnets), Check: v => CheckEach(v, i => Ipv4Network.TryParse(i, out _) ? null : $"'{i}' is not a network such as 192.168.10.0/24")),
        new("Wins:Security:RequireAddressMatchesSource", SecSecurity, "Only a host's own address", Kind.Bool, true,
            o => Flag(o.RequireAddressMatchesSource)),
        new("Wins:Security:BlockedNames", SecSecurity, "Blocked names", Kind.List, true,
            o => string.Join(", ", o.BlockedNames), Check: v => CheckEach(v, CheckNetbiosName)),
        new("Wins:Security:AllowMultihomedMerge", SecSecurity, "Accept a second address for one name", Kind.Bool, true,
            o => Flag(o.AllowMultihomedMerge)),
        new("Wins:Security:MaxDynamicNames", SecSecurity, "Maximum registered names", Kind.Int, true,
            o => Num(o.MaxDynamicNames), 0, 10_000_000, Unit: "names"),
        new("Wins:Security:MaxNamesPerAddress", SecSecurity, "Maximum names per address", Kind.Int, true,
            o => Num(o.MaxNamesPerAddress), 0, 100_000, Unit: "names"),
        new("Wins:Security:RateLimitPerSecond", SecSecurity, "Requests per second per address", Kind.Int, false,
            o => Num(o.RateLimitPerSecond), 0, 1_000_000, Unit: "per second"),
        new("Wins:Security:RateLimitBurst", SecSecurity, "Burst allowance per address", Kind.Int, false,
            o => Num(o.RateLimitBurst), 1, 10_000_000, Unit: "packets"),
        new("Wins:Security:MaxConcurrentChallenges", SecSecurity, "Concurrent name challenges", Kind.Int, false,
            o => Num(o.MaxConcurrentChallenges), 1, 10_000),

        // ----- DNS fallback
        new("Wins:Dns:Enabled", SecDns, "Use DNS for unknown names", Kind.Bool, true,
            o => Flag(o.DnsEnabled)),
        new("Wins:Dns:Servers", SecDns, "DNS servers", Kind.List, true,
            o => string.Join(", ", o.DnsServers.Select(s => s.ToString())), Check: v => CheckEach(v, CheckIpv4)),
        new("Wins:Dns:Suffix", SecDns, "DNS suffix", Kind.Text, true,
            o => o.DnsSuffix, Check: CheckDnsSuffix),
        new("Wins:Dns:TimeoutMs", SecDns, "DNS timeout", Kind.Int, true,
            o => Num(o.DnsTimeoutMs), 100, 30_000, Unit: "ms"),
        new("Wins:Dns:CacheSeconds", SecDns, "Remember found names for", Kind.Int, true,
            o => Num(o.DnsCacheSeconds), 0, 86_400, Unit: "seconds"),
        new("Wins:Dns:NegativeCacheSeconds", SecDns, "Remember missing names for", Kind.Int, true,
            o => Num(o.DnsNegativeCacheSeconds), 0, 86_400, Unit: "seconds"),

        // ----- Replication
        new("Wins:ReplicationPort", SecRepl, "Replication TCP port", Kind.Int, false,
            o => Num(o.ReplicationPort), 1, 65535),

        // ----- Dashboard
        new(WinsOptions.DashboardRemoteAccessKey, SecDash, "Open the dashboard to other computers", Kind.Bool, false,
            o => Flag(o.DashboardRemoteAccess)),
        new("Dashboard:RequireSignInToView", SecDash, "Sign-in required to view", Kind.Bool, true,
            o => Flag(o.RequireSignInToView)),
        new("Dashboard:AllowedHosts", SecDash, "Extra host names", Kind.List, true,
            o => string.Join(", ", o.DashboardAllowedHosts), Check: v => CheckEach(v, CheckHostName)),
        new(AdminTokenKey, SecDash, "API token for scripts", Kind.Secret, true,
            o => o.AdminToken, Check: v => v.Length == 0 || v.Length >= MinAdminTokenLength ? null : $"must be at least {MinAdminTokenLength} characters (or empty to switch it off)"),
    ];

    private readonly IConfiguration _config;
    private readonly SettingsOverlay _overlay;
    private readonly WinsOptions _live;
    private readonly DnsFallbackResolver _dns;
    private readonly ILogger<SettingsService> _logger;
    private readonly Lock _lock = new();
    // What the configuration says now = what the service will run with after its next start.
    private WinsOptions _saved;

    public SettingsService(IConfiguration config, SettingsOverlay overlay, WinsOptions live, DnsFallbackResolver dns, ILogger<SettingsService> logger)
    {
        _config = config;
        _overlay = overlay;
        _live = live;
        _dns = dns;
        _logger = logger;
        _saved = WinsOptions.FromConfiguration(config);

        if (overlay.LoadError is { } error)
            logger.LogError("{File} could not be read ({Error}) - settings saved from the dashboard are not in effect", overlay.FilePath, error);
    }

    public SettingsResponse Describe()
    {
        lock (_lock)
        {
            var defaults = new WinsOptions();
            var items = new List<SettingDto>(Catalogue.Length);
            var pending = new List<string>();

            foreach (var def in Catalogue)
            {
                bool secret = def.Kind == Kind.Secret;
                string saved = def.Get(_saved), running = def.Get(_live);
                bool waiting = !def.Hot && saved != running;
                if (waiting) pending.Add(def.Key);

                items.Add(new SettingDto(def.Key, def.Section, def.Label, def.Kind.ToString().ToLowerInvariant(),
                    secret ? "" : saved, secret ? "" : running, secret ? "" : def.Get(defaults),
                    def.Kind == Kind.Int ? def.Min : null, def.Kind == Kind.Int ? def.Max : null, def.Options, def.Unit,
                    RestartRequired: !def.Hot, Pending: waiting, IsSet: secret ? saved.Length > 0 : null));
            }
            return new SettingsResponse(items, pending, _overlay.FilePath, ServiceControl.CanRestart, ServiceControl.Manager);
        }
    }

    /// <summary>
    /// Validates and saves the given settings (config key → value as text; only the ones that
    /// changed need to be sent). Nothing is saved unless every value is acceptable.
    /// Returns null on success, or what is wrong.
    /// </summary>
    public string? Save(IReadOnlyDictionary<string, string>? changes)
    {
        if (changes is null || changes.Count == 0) return "Nothing to save.";

        lock (_lock)
        {
            var before = _overlay.Snapshot();
            var candidate = new Dictionary<string, string>(before, StringComparer.OrdinalIgnoreCase);
            var touched = new List<Def>();

            foreach (var (key, raw) in changes)
            {
                var def = Array.Find(Catalogue, d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (def is null) return $"'{key}' is not a setting that can be changed here.";

                string value = (raw ?? "").Trim();
                if (Validate(def, ref value) is { } problem) return $"{def.Label}: {problem}";
                candidate[def.Key] = value;
                touched.Add(def);
            }

            // Read the whole configuration back with the new values in place, exactly as a restart would.
            _overlay.Replace(candidate);
            var fresh = WinsOptions.FromConfiguration(_config);
            string? error = CrossCheck(fresh, candidate, touched);

            if (error is null)
            {
                try { _overlay.Write(candidate); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    error = $"Could not write {_overlay.FilePath}: {ex.Message}";
                }
            }

            if (error is not null)
            {
                _overlay.Replace(before);
                return error;
            }

            _saved = fresh;
            _live.ApplyHot(fresh);
            if (touched.Exists(d => d.Section == SecDns)) _dns.ClearCache(); // answers cached under the old DNS settings

            _logger.LogWarning("Settings changed from the dashboard: {Keys}",
                string.Join(", ", touched.Select(d => d.Kind == Kind.Secret ? d.Key : $"{d.Key} = {candidate[d.Key]}")));
            return null;
        }
    }

    // One value on its own: the right kind, inside its limits. May normalise the text it is given.
    private static string? Validate(Def def, ref string value)
    {
        switch (def.Kind)
        {
            case Kind.Bool:
                if (!bool.TryParse(value, out bool flag)) return "must be true or false.";
                value = Flag(flag);
                return null;
            case Kind.Int:
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number)) return "must be a whole number.";
                if (number < def.Min || number > def.Max) return $"must be between {def.Min} and {def.Max}.";
                value = Num(number);
                return null;
            case Kind.Choice:
                string given = value;
                if (Array.Find(def.Options!, o => o.Equals(given, StringComparison.OrdinalIgnoreCase)) is not { } option)
                    return $"must be one of {string.Join(", ", def.Options!)}.";
                value = option;
                return null;
            case Kind.List:
                value = string.Join(",", value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                break;
        }

        if (value.Length > 2000) return "is too long.";
        if (def.Check?.Invoke(value) is { } problem) return problem.TrimEnd('.') + ".";
        if (def.Kind == Kind.List && value.Length == 0) value = WinsOptions.EmptyList; // an empty value would let appsettings.json's list show through
        return null;
    }

    // Rules that span several settings, checked against the configuration as a restart would read it.
    private string? CrossCheck(WinsOptions fresh, Dictionary<string, string> candidate, List<Def> touched)
    {
        if (candidate.TryGetValue(TtlKeyMin, out string? min) && uint.TryParse(min, out uint minTtl) && minTtl > fresh.MaxTtlSeconds)
            return "Minimum registration lifetime cannot be longer than the maximum.";
        if (fresh.RegistrationMode == RegistrationMode.AllowedSubnets && fresh.AllowedSubnets.Length == 0)
            return "Who may register names: AllowedSubnets needs at least one entry in Allowed subnets - with an empty list nobody could register.";
        if (fresh.RateLimitPerSecond > 0 && fresh.RateLimitBurst < fresh.RateLimitPerSecond)
            return "Burst allowance per address cannot be smaller than the requests per second.";

        // A value that does not come back was overridden further up the chain (an environment
        // variable or a command-line argument): saving it would silently do nothing.
        foreach (var def in touched)
            if (def.Kind != Kind.List && !def.Get(fresh).Equals(candidate[def.Key], StringComparison.OrdinalIgnoreCase))
                return $"{def.Label}: this setting is fixed by an environment variable or command-line argument and cannot be changed here.";
        return null;
    }

    // ---------- value formats ----------

    private static string Flag(bool value) => value ? "true" : "false";
    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? CheckEach(string list, Func<string, string?> check)
    {
        foreach (string item in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (check(item) is { } problem) return problem;
        return null;
    }

    private static string? CheckIpv4(string value) =>
        IPAddress.TryParse(value, out var address) && address.AddressFamily == AddressFamily.InterNetwork && value.Count(c => c == '.') == 3
            ? null : $"'{value}' is not an IPv4 address";

    private static string? CheckNetbiosName(string value) =>
        NameKey.TryCreate(value, 0, out _) ? null : $"'{value}' is not a NetBIOS name (1-{NameKey.MaxNameChars} printable ASCII characters)";

    private static string? CheckSelfSuffixes(string list)
    {
        var items = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (items.Length == 0) return "needs at least one suffix, e.g. 00, 20";
        foreach (string item in items)
        {
            if (item.Length != 2 || !byte.TryParse(item, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte suffix))
                return $"'{item}' is not a two-digit hex suffix such as 00 or 20";
            if (suffix is 0x1B or 0x1C or 0x1D)
                return $"{item.ToUpperInvariant()} is a domain / browser role this service does not perform - add a static mapping instead";
        }
        return null;
    }

    private static string? CheckHostName(string value) =>
        value.Length <= 253 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_') ? null : $"'{value}' is not a host name";

    private static string? CheckDnsSuffix(string value) =>
        value.Length == 0 || (CheckHostName(value) is null && value[0] != '.' && value[^1] != '.') ? null : $"'{value}' is not a DNS domain such as corp.local";
}
