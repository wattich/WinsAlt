using System.Net;
using System.Net.Sockets;

namespace WinsAlt.Core.Domain;

/// <summary>What to do when a unique name is registered from an address other than its owner's.</summary>
public enum ConflictPolicy
{
    /// <summary>WINS behaviour: query the current owner; it keeps the name only if it answers.</summary>
    Challenge,
    /// <summary>The newest registration always wins (no challenge).</summary>
    Overwrite,
    /// <summary>The existing owner always wins until its registration expires or is released.</summary>
    Reject
}

/// <summary>Who may register names dynamically.</summary>
public enum RegistrationMode
{
    /// <summary>Classic WINS: any host that reaches UDP 137 may register (the compatible default).</summary>
    Dynamic,
    /// <summary>Only hosts inside <see cref="WinsOptions.AllowedSubnets"/> may register, refresh or release.</summary>
    AllowedSubnets,
    /// <summary>No dynamic registration at all - only static mappings (and replicas) resolve.</summary>
    StaticOnly
}

/// <summary>An IPv4 network in host-order numeric form, so a membership test is one AND and one compare.</summary>
public readonly record struct Ipv4Network(uint Network, uint Mask)
{
    public bool Contains(uint address) => (address & Mask) == Network;

    /// <summary>Parses "192.168.10.0/24" (a bare address means /32).</summary>
    public static bool TryParse(string? text, out Ipv4Network network)
    {
        network = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Trim().Split('/');
        int prefix = 32;
        if (parts.Length > 2 || !Ipv4.TryParse(parts[0], out uint address)) return false;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out prefix) || prefix is < 0 or > 32)) return false;

        uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        network = new Ipv4Network(address & mask, mask);
        return true;
    }

    public override string ToString() => $"{Ipv4.ToString(Network)}/{System.Numerics.BitOperations.PopCount(Mask)}";
}

/// <summary>
/// Settings from the "Wins" section of appsettings.json. Values are read key-by-key from
/// <see cref="IConfiguration"/> instead of <c>Bind()</c>/<c>Get&lt;T&gt;()</c> - the binder is
/// reflection based and is exactly the kind of thing that breaks under Native AOT trimming.
/// </summary>
public sealed class WinsOptions
{
    public IPAddress ListenAddress { get; private set; } = IPAddress.Any;
    public int Port { get; private set; } = 137;
    public int Workers { get; private set; } = Math.Clamp(Environment.ProcessorCount, 1, 4);
    public int QueueCapacity { get; private set; } = 4096;
    public bool AnswerBroadcasts { get; private set; }

    public uint MinTtlSeconds { get; private set; } = 21_600;       // 6 hours
    public uint MaxTtlSeconds { get; private set; } = 518_400;      // 6 days - the WINS default renewal interval
    public int SweepIntervalSeconds { get; private set; } = 30;
    public int PersistIntervalSeconds { get; private set; } = 30;
    public ConflictPolicy ConflictPolicy { get; private set; } = ConflictPolicy.Challenge;
    public int QueryLogCapacity { get; private set; } = 1000;
    /// <summary>
    /// Port a name's current owner is challenged on. Always 137 in production (that is where
    /// NetBIOS nodes listen); only the test suite changes it, because Windows will not deliver
    /// loopback traffic for port 137 to a test socket.
    /// </summary>
    public int ChallengePort { get; private set; } = 137;
    public string DataDirectory { get; private set; } = AppContext.BaseDirectory;

    public bool DnsEnabled { get; private set; } = true;
    public IPAddress[] DnsServers { get; private set; } = [];
    public string DnsSuffix { get; private set; } = "";
    public int DnsTimeoutMs { get; private set; } = 2000;
    public int DnsCacheSeconds { get; private set; } = 300;
    public int DnsNegativeCacheSeconds { get; private set; } = 60;

    /// <summary>Keep this machine's own name resolvable (it cannot register itself once NetBT is off).</summary>
    /// <summary>TCP port other WinsAlt servers pull this server's names from.</summary>
    public int ReplicationPort { get; private set; } = 8138;

    public bool RegisterSelf { get; private set; } = true;
    /// <summary>NetBIOS name to publish for this machine; empty = the computer name.</summary>
    public string SelfName { get; private set; } = "";
    /// <summary>Suffixes this machine's own name is published under (00 workstation, 20 file server by default).</summary>
    public byte[] SelfSuffixes { get; private set; } = [0x00, 0x20];
    /// <summary>Workgroup / domain to announce membership of; empty = ask Windows, "-" = none.</summary>
    public string SelfGroup { get; private set; } = "";

    // ----- Security (section Wins:Security). Every restriction that could lock out a working
    // network defaults to off; the ones that cannot (size cap, classic poisoning names, flood
    // guards with generous limits) default to on. -----
    public RegistrationMode RegistrationMode { get; private set; } = RegistrationMode.Dynamic;
    public Ipv4Network[] AllowedSubnets { get; private set; } = [];
    /// <summary>Refuse a registration/release whose record names an address other than the packet's source.</summary>
    public bool RequireAddressMatchesSource { get; private set; }
    /// <summary>Names (any suffix) that may never be registered dynamically or resolved through DNS / partners.</summary>
    public string[] BlockedNames { get; private set; } = ["WPAD", "ISATAP"];
    /// <summary>The same names as 16-byte keys with the suffix byte cleared, for an allocation-free lookup.</summary>
    public HashSet<NameKey> BlockedNameKeys { get; private set; } = new();
    public int MaxDynamicNames { get; private set; } = 100_000;
    public int MaxNamesPerAddress { get; private set; } = 64;
    /// <summary>Sustained requests per second accepted from one source address (0 = unlimited).</summary>
    public int RateLimitPerSecond { get; private set; } = 100;
    public int RateLimitBurst { get; private set; } = 300;
    public int MaxConcurrentChallenges { get; private set; } = 64;
    /// <summary>Accept a second address for a unique name when its current owner vouches for it (LAN + Wi-Fi laptops).</summary>
    public bool AllowMultihomedMerge { get; private set; } = true;

    public string AdminToken { get; private set; } = "";
    /// <summary>Extra Host header values the dashboard answers to (IP literals, localhost and the machine name always are).</summary>
    public string[] DashboardAllowedHosts { get; private set; } = [];
    /// <summary>Require sign-in (or the admin token) even to view - from anywhere, this machine included. On by default.</summary>
    public bool RequireSignInToView { get; private set; } = true;
    /// <summary>TCP port the dashboard listens on (from the Kestrel endpoint URL).</summary>
    public int DashboardPort { get; private set; } = 8137;
    /// <summary>True when the dashboard listens on more than the loopback address, i.e. other computers can open it.</summary>
    public bool DashboardRemoteAccess { get; private set; }

    public const string DashboardUrlKey = "Kestrel:Endpoints:Management:Url";
    public const string DashboardRemoteAccessKey = "Dashboard:RemoteAccess";

    /// <summary>
    /// The dashboard's endpoint as configured: scheme, host and port of the Kestrel URL
    /// ("http://127.0.0.1:8137"; Kestrel's "*" and "+" hosts count as every address).
    /// </summary>
    private static (string Scheme, string Host, int Port) DashboardEndpoint(IConfiguration config)
    {
        string url = (config[DashboardUrlKey] ?? "").Trim().Replace("://*", "://0.0.0.0").Replace("://+", "://0.0.0.0");
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Port > 0
            ? (uri.Scheme, uri.Host.Trim('[', ']'), uri.Port)
            : ("http", "127.0.0.1", 8137);
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));

    /// <summary>
    /// The URL Kestrel must listen on when <c>Dashboard:RemoteAccess</c> (the Settings page switch)
    /// disagrees with the configured URL's host - or null when there is nothing to change. Only the
    /// host is replaced; the port stays whatever appsettings.json (the installer) chose.
    /// </summary>
    public static string? DashboardUrlOverride(IConfiguration config)
    {
        if (!bool.TryParse(config[DashboardRemoteAccessKey], out bool remote)) return null;
        var (scheme, host, port) = DashboardEndpoint(config);
        return remote == !IsLoopbackHost(host) ? null : $"{scheme}://{(remote ? "0.0.0.0" : "127.0.0.1")}:{port}";
    }

    /// <summary>True when <paramref name="key"/> (whatever its suffix) is on the blocked-name list.</summary>
    public bool IsBlockedName(NameKey key)
    {
        var blocked = BlockedNameKeys; // one read: the set can be swapped by a settings change
        return blocked.Count > 0 && blocked.Contains(new NameKey(key.Hi, key.Lo & ~0xFFUL));
    }

    public static WinsOptions FromConfiguration(IConfiguration config)
    {
        var o = new WinsOptions();
        var wins = config.GetSection("Wins");

        if (IPAddress.TryParse(wins["ListenAddress"], out var listen) && listen.AddressFamily == AddressFamily.InterNetwork)
            o.ListenAddress = listen;
        o.Port = Int(wins["Port"], o.Port, 1, 65535);
        // 0 (the shipped default) = auto: one worker per core, capped at 4.
        if (Int(wins["Workers"], 0, 0, 64) is > 0 and var workers) o.Workers = workers;
        o.QueueCapacity = Int(wins["QueueCapacity"], o.QueueCapacity, 64, 1_000_000);
        o.AnswerBroadcasts = Bool(wins["AnswerBroadcasts"], o.AnswerBroadcasts);

        o.MaxTtlSeconds = (uint)Int(wins["MaxTtlSeconds"], (int)o.MaxTtlSeconds, 60, 31_536_000);
        o.MinTtlSeconds = (uint)Int(wins["MinTtlSeconds"], (int)Math.Min(o.MinTtlSeconds, o.MaxTtlSeconds), 30, (int)o.MaxTtlSeconds);
        o.SweepIntervalSeconds = Int(wins["SweepIntervalSeconds"], o.SweepIntervalSeconds, 1, 3600);
        o.PersistIntervalSeconds = Int(wins["PersistIntervalSeconds"], o.PersistIntervalSeconds, 1, 3600);
        o.QueryLogCapacity = Int(wins["QueryLogCapacity"], o.QueryLogCapacity, 16, 100_000);
        if (Enum.TryParse<ConflictPolicy>(wins["ConflictPolicy"], ignoreCase: true, out var policy)) o.ConflictPolicy = policy;
        if (wins["DataDirectory"] is { Length: > 0 } dir) o.DataDirectory = Path.GetFullPath(dir, AppContext.BaseDirectory);

        o.ChallengePort = Int(wins["ChallengePort"], o.ChallengePort, 1, 65535);
        o.ReplicationPort = Int(wins["ReplicationPort"], o.ReplicationPort, 1, 65535);
        o.RegisterSelf = Bool(wins["RegisterSelf"], o.RegisterSelf);
        o.SelfName = (wins["SelfName"] ?? "").Trim();
        o.SelfGroup = (wins["SelfGroup"] ?? "").Trim();

        var selfSuffixes = new List<byte>();
        foreach (string item in List(wins.GetSection("SelfSuffixes")))
            if (byte.TryParse(item, System.Globalization.NumberStyles.HexNumber, null, out byte suffix) && !selfSuffixes.Contains(suffix))
                selfSuffixes.Add(suffix);
        // 1B / 1C / 1D are roles (domain master browser, domain controllers, master browser) this
        // service does not perform - announcing them would misdirect clients, so they are not accepted here.
        selfSuffixes.RemoveAll(s => s is 0x1B or 0x1C or 0x1D);
        if (selfSuffixes.Count > 0) o.SelfSuffixes = selfSuffixes.ToArray();

        var dns = wins.GetSection("Dns");
        o.DnsEnabled = Bool(dns["Enabled"], o.DnsEnabled);
        o.DnsSuffix = (dns["Suffix"] ?? "").Trim().Trim('.');
        o.DnsTimeoutMs = Int(dns["TimeoutMs"], o.DnsTimeoutMs, 100, 30_000);
        o.DnsCacheSeconds = Int(dns["CacheSeconds"], o.DnsCacheSeconds, 0, 86_400);
        o.DnsNegativeCacheSeconds = Int(dns["NegativeCacheSeconds"], o.DnsNegativeCacheSeconds, 0, 86_400);

        var servers = new List<IPAddress>();
        foreach (string item in List(dns.GetSection("Servers")))
            if (IPAddress.TryParse(item, out var server)) servers.Add(server);
        o.DnsServers = servers.ToArray();

        var security = wins.GetSection("Security");
        if (Enum.TryParse<RegistrationMode>(security["RegistrationMode"], ignoreCase: true, out var mode)) o.RegistrationMode = mode;
        o.RequireAddressMatchesSource = Bool(security["RequireAddressMatchesSource"], o.RequireAddressMatchesSource);
        o.MaxDynamicNames = Int(security["MaxDynamicNames"], o.MaxDynamicNames, 0, 10_000_000);
        o.MaxNamesPerAddress = Int(security["MaxNamesPerAddress"], o.MaxNamesPerAddress, 0, 100_000);
        o.RateLimitPerSecond = Int(security["RateLimitPerSecond"], o.RateLimitPerSecond, 0, 1_000_000);
        o.RateLimitBurst = Int(security["RateLimitBurst"], Math.Max(o.RateLimitBurst, o.RateLimitPerSecond), 1, 10_000_000);
        o.MaxConcurrentChallenges = Int(security["MaxConcurrentChallenges"], o.MaxConcurrentChallenges, 1, 10_000);
        o.AllowMultihomedMerge = Bool(security["AllowMultihomedMerge"], o.AllowMultihomedMerge);

        var subnets = new List<Ipv4Network>();
        foreach (string item in List(security.GetSection("AllowedSubnets")))
            if (Ipv4Network.TryParse(item, out var network)) subnets.Add(network);
        o.AllowedSubnets = subnets.ToArray();

        // An explicit list replaces the built-in one (an explicit empty list switches the feature off).
        var blocked = security.GetSection("BlockedNames");
        if (blocked.Exists())
            o.BlockedNames = List(blocked).Select(v => v.ToUpperInvariant()).ToArray();
        foreach (string name in o.BlockedNames)
            if (NameKey.TryCreate(name, 0, out var blockedKey)) o.BlockedNameKeys.Add(blockedKey);

        o.AdminToken = (config["Dashboard:AdminToken"] ?? "").Trim();
        o.RequireSignInToView = Bool(config["Dashboard:RequireSignInToView"], o.RequireSignInToView);
        o.DashboardAllowedHosts = List(config.GetSection("Dashboard:AllowedHosts")).ToArray();

        var (_, dashboardHost, dashboardPort) = DashboardEndpoint(config);
        o.DashboardPort = dashboardPort;
        o.DashboardRemoteAccess = Bool(config[DashboardRemoteAccessKey], !IsLoopbackHost(dashboardHost));
        return o;
    }

    /// <summary>
    /// A list setting: a JSON array in appsettings.json, or one comma-separated value - the form
    /// the dashboard saves in settings.json. The single value wins when both exist: layering a
    /// shorter array over a longer one would otherwise leave the longer one's tail in place.
    /// An empty value does not count (that is what an empty JSON array reads as), so the dashboard
    /// saves "no entries" as <see cref="EmptyList"/>.
    /// </summary>
    public const string EmptyList = ",";

    private static IEnumerable<string> List(IConfigurationSection section) =>
        section.Value is { Length: > 0 } joined
            ? joined.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : section.GetChildren().Select(c => (c.Value ?? "").Trim()).Where(v => v.Length > 0);

    /// <summary>
    /// Takes over, from a freshly read configuration, every setting that is looked up again on each
    /// use - so a change made in the dashboard works without a restart. Settings that were turned
    /// into sockets, buffers or timers at startup (listen address, ports, queue and log sizes, rate
    /// limit, this server's own name) are left alone: they need a restart.
    /// Arrays and sets are replaced whole, never edited in place - the packet path reads them without a lock.
    /// </summary>
    public void ApplyHot(WinsOptions fresh)
    {
        AnswerBroadcasts = fresh.AnswerBroadcasts;
        ConflictPolicy = fresh.ConflictPolicy;
        // Widen first, narrow second: a worker reading between the two writes never sees min > max.
        if (fresh.MaxTtlSeconds >= MaxTtlSeconds) { MaxTtlSeconds = fresh.MaxTtlSeconds; MinTtlSeconds = fresh.MinTtlSeconds; }
        else { MinTtlSeconds = fresh.MinTtlSeconds; MaxTtlSeconds = fresh.MaxTtlSeconds; }

        // The subnet list goes in before the mode that starts consulting it.
        AllowedSubnets = fresh.AllowedSubnets;
        RegistrationMode = fresh.RegistrationMode;
        RequireAddressMatchesSource = fresh.RequireAddressMatchesSource;
        BlockedNames = fresh.BlockedNames;
        BlockedNameKeys = fresh.BlockedNameKeys;
        AllowMultihomedMerge = fresh.AllowMultihomedMerge;
        MaxDynamicNames = fresh.MaxDynamicNames;
        MaxNamesPerAddress = fresh.MaxNamesPerAddress;

        DnsEnabled = fresh.DnsEnabled;
        DnsServers = fresh.DnsServers;
        DnsSuffix = fresh.DnsSuffix;
        DnsTimeoutMs = fresh.DnsTimeoutMs;
        DnsCacheSeconds = fresh.DnsCacheSeconds;
        DnsNegativeCacheSeconds = fresh.DnsNegativeCacheSeconds;

        AdminToken = fresh.AdminToken;
        DashboardAllowedHosts = fresh.DashboardAllowedHosts;
        RequireSignInToView = fresh.RequireSignInToView;
    }

    private static int Int(string? value, int fallback, int min, int max) =>
        int.TryParse(value, out int parsed) ? Math.Clamp(parsed, min, max) : fallback;

    private static bool Bool(string? value, bool fallback) =>
        bool.TryParse(value, out bool parsed) ? parsed : fallback;
}
