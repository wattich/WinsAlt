using System.Diagnostics;
using WinsAlt.Core.Domain;

namespace WinsAlt.Infrastructure;

/// <summary>One inbound port this server needs open, and whether the firewall lets it in.</summary>
public sealed record FirewallRule(string Id, string Name, string Purpose, string Protocol, int Port, bool Allowed);

/// <summary>
/// The firewall openings WinsAlt needs for other machines to reach it: the name service, the
/// replication port and the dashboard. Each operating system's own tool does the work - the
/// firewalls' programmatic APIs (COM on Windows, D-Bus for firewalld) are not usable from a
/// Native AOT binary without reflection:
///
///  - Windows: netsh.exe <c>advfirewall firewall</c> (Windows 7 on). Rules are found by name alone  - 
///    netsh's text output is localised, so it is never parsed - and are scoped to this executable.
///    The first two carry the names the installer gives them, so the installer's rules show up here.
///  - Linux with firewalld (CentOS Stream, RHEL family): <c>firewall-cmd --query-port</c> /
///    <c>--add-port</c> / <c>--remove-port</c> in the default zone, applied to the running firewall
///    and to the permanent configuration. Exit codes only.
///  - Linux with ufw (Debian, Ubuntu): <c>ufw allow</c> / <c>ufw delete allow</c>; whether a port is
///    allowed is read from <c>ufw status</c> (ufw has no query command; its output is not translated).
///  - Linux with neither active: nothing to manage - reported as such, nothing is blocked by them.
///
/// Changing the firewall needs administrator rights (LocalSystem on Windows, root on Linux).
/// </summary>
public sealed class FirewallService
{
    public enum Kind { None, WindowsFirewall, Firewalld, Ufw }

    private sealed record Spec(string Id, string Name, string Purpose, string Protocol, Func<WinsOptions, int> Port);

    private static readonly Spec[] Specs =
    [
        new("dashboard", "WinsAlt dashboard", "This dashboard, from other computers", "TCP", o => o.DashboardPort),
        new("nbns", "WinsAlt NBNS (UDP 137)", "Name service - clients register and look up names", "UDP", o => o.Port),
        new("replication", "WinsAlt replication (TCP 8138)", "Replication - other WinsAlt servers copy names from here", "TCP", o => o.ReplicationPort),
    ];

    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(10);

    private readonly WinsOptions _options;
    private readonly ILogger<FirewallService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<FirewallRule>? _cached;
    private DateTime _cachedAt;
    private Kind? _kind;
    private DateTime _kindAt;
    private string? _tool;

    public FirewallService(WinsOptions options, ILogger<FirewallService> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Which firewall this machine uses. Looked up again once the answer is a few seconds old: an admin
    /// commonly runs <c>ufw enable</c> after installing, and the panel must not keep saying "none" until a restart.
    /// </summary>
    public async Task<Kind> DetectAsync(CancellationToken ct)
    {
        if (_kind is { } known && (OperatingSystem.IsWindows() || DateTime.UtcNow - _kindAt < CacheFor)) return known;

        Kind kind = Kind.None;
        if (OperatingSystem.IsWindows())
        {
            _tool = Path.Combine(Environment.SystemDirectory, "netsh.exe");
            kind = Kind.WindowsFirewall;
        }
        else if (FindTool("firewall-cmd") is { } firewallCmd && await RunAsync(firewallCmd, ["--state"], ct) == 0)
        {
            _tool = firewallCmd;
            kind = Kind.Firewalld;
        }
        else if (FindTool("ufw") is { } ufw && (await RunAsync(ufw, ["status"], ct, capture: true)).Output.StartsWith("Status: active", StringComparison.Ordinal))
        {
            _tool = ufw;
            kind = Kind.Ufw;
        }

        if (kind != _kind) _cached = null;
        _kind = kind;
        _kindAt = DateTime.UtcNow;
        return kind;
    }

    public static string DisplayName(Kind kind) => kind switch
    {
        Kind.WindowsFirewall => "Windows Firewall",
        Kind.Firewalld => "firewalld",
        Kind.Ufw => "ufw",
        _ => "none"
    };

    /// <summary>The rules and their state. Each lookup starts a process, so the answer is reused for a few seconds.</summary>
    public async Task<IReadOnlyList<FirewallRule>> ListAsync(CancellationToken ct)
    {
        Kind kind = await DetectAsync(ct);
        if (kind == Kind.None) return [];

        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is not null && DateTime.UtcNow - _cachedAt < CacheFor) return _cached;

            string ufwStatus = kind == Kind.Ufw ? (await RunAsync(_tool!, ["status"], ct, capture: true)).Output : "";
            var rules = new List<FirewallRule>(Specs.Length);
            foreach (var spec in Specs)
            {
                int port = spec.Port(_options);
                bool allowed = kind switch
                {
                    Kind.WindowsFirewall => await NetshAsync($"advfirewall firewall show rule name=\"{spec.Name}\"", ct) == 0,
                    Kind.Firewalld => await RunAsync(_tool!, [$"--query-port={PortSpec(spec, port)}"], ct) == 0,
                    _ => UfwAllows(ufwStatus, PortSpec(spec, port))
                };
                rules.Add(new FirewallRule(spec.Id, RuleName(kind, spec, port), spec.Purpose, spec.Protocol, port, allowed));
            }
            _cached = rules;
            _cachedAt = DateTime.UtcNow;
            return rules;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Opens or closes one port. Returns an error message, or null on success.</summary>
    public async Task<string?> SetAsync(string id, bool allow, CancellationToken ct)
    {
        // Nothing from the request reaches a command line: the id only selects one of the fixed rules.
        if (Array.Find(Specs, s => s.Id == id) is not { } spec) return "Unknown firewall rule.";

        try
        {
            Kind kind = await DetectAsync(ct);
            if (kind == Kind.None) return "No active firewall (firewalld or ufw) was found on this server, so there is nothing to change.";

            await _gate.WaitAsync(ct);
            try
            {
                _cached = null;
                int port = spec.Port(_options);
                string? error = kind switch
                {
                    Kind.WindowsFirewall => await SetWindowsAsync(spec, port, allow, ct),
                    Kind.Firewalld => await SetFirewalldAsync(spec, port, allow, ct),
                    _ => await SetUfwAsync(spec, port, allow, ct)
                };
                if (error is null)
                    _logger.LogWarning("Firewall ({Firewall}): inbound {Protocol} {Port} for {Purpose} {Action} from the dashboard",
                        DisplayName(kind), spec.Protocol, port, spec.Id, allow ? "allowed" : "closed");
                return error;
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) { return "Timed out waiting for the firewall."; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return "Could not run the firewall tool: " + ex.Message;
        }
    }

    // ---------- Windows: netsh ----------

    private async Task<string?> SetWindowsAsync(Spec spec, int port, bool allow, CancellationToken ct)
    {
        if (Environment.ProcessPath is not { } exe) return "Could not determine this program's path.";

        // Remove first either way: a leftover rule for an old port would otherwise stay behind.
        int removed = await NetshAsync($"advfirewall firewall delete rule name=\"{spec.Name}\"", ct);
        if (!allow)
            return removed != 0 && await NetshAsync($"advfirewall firewall show rule name=\"{spec.Name}\"", ct) == 0
                ? "Windows refused to remove the rule. The service needs administrator rights to change the firewall."
                : null;

        int added = await NetshAsync(
            $"advfirewall firewall add rule name=\"{spec.Name}\" dir=in action=allow protocol={spec.Protocol} localport={port} program=\"{exe}\" profile=any", ct);
        return added == 0 ? null
            : "Windows refused to add the rule. The service needs administrator rights to change the firewall (it has them when installed as a service).";
    }

    // ---------- Linux: firewalld ----------

    private async Task<string?> SetFirewalldAsync(Spec spec, int port, bool allow, CancellationToken ct)
    {
        string action = allow ? "--add-port" : "--remove-port";
        string target = $"{action}={PortSpec(spec, port)}";
        // Runtime first (takes effect now), then the permanent configuration (survives a reload / reboot).
        // Asking for what is already true fails with ALREADY_ENABLED / NOT_ENABLED; only the end state counts.
        await RunAsync(_tool!, [target], ct);
        await RunAsync(_tool!, ["--permanent", target], ct);

        bool now = await RunAsync(_tool!, [$"--query-port={PortSpec(spec, port)}"], ct) == 0;
        bool saved = await RunAsync(_tool!, ["--permanent", $"--query-port={PortSpec(spec, port)}"], ct) == 0;
        return now == allow && saved == allow ? null
            : "firewalld did not accept the change. The service must run as root to change the firewall.";
    }

    // ---------- Linux: ufw ----------

    private async Task<string?> SetUfwAsync(Spec spec, int port, bool allow, CancellationToken ct)
    {
        string portSpec = PortSpec(spec, port);
        int exit = allow
            ? await RunAsync(_tool!, ["allow", portSpec, "comment", spec.Name], ct)
            : await RunAsync(_tool!, ["delete", "allow", portSpec], ct);

        bool nowAllowed = UfwAllows((await RunAsync(_tool!, ["status"], ct, capture: true)).Output, portSpec);
        return nowAllowed == allow ? null
            : exit != 0 ? "ufw did not accept the change. The service must run as root to change the firewall."
            : "ufw reports the change as made, but the port's state did not change.";
    }

    /// <summary>True when a line of <c>ufw status</c> allows the port from anywhere ("137/udp   ALLOW   Anywhere").</summary>
    private static bool UfwAllows(string status, string portSpec)
    {
        foreach (string line in status.Split('\n'))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 2 && fields[0] == portSpec && fields[1] == "ALLOW") return true;
        }
        return false;
    }

    // ---------- shared ----------

    private static string PortSpec(Spec spec, int port) => $"{port}/{spec.Protocol.ToLowerInvariant()}";

    private static string RuleName(Kind kind, Spec spec, int port) => kind switch
    {
        Kind.WindowsFirewall => spec.Name,
        Kind.Firewalld => $"firewalld: {PortSpec(spec, port)} in the default zone",
        _ => $"ufw: {PortSpec(spec, port)}"
    };

    private static string? FindTool(string name)
    {
        foreach (string dir in new[] { "/usr/sbin", "/usr/bin", "/sbin", "/bin" })
        {
            string path = Path.Combine(dir, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    // netsh has its own name="value" syntax, which the per-argument quoting of ArgumentList would break.
    private async Task<int> NetshAsync(string arguments, CancellationToken ct) =>
        (await RunAsync(new ProcessStartInfo { FileName = _tool!, Arguments = arguments }, ct, capture: false)).ExitCode;

    private static async Task<int> RunAsync(string tool, string[] arguments, CancellationToken ct) =>
        (await RunAsync(tool, arguments, ct, capture: false)).ExitCode;

    private static Task<(int ExitCode, string Output)> RunAsync(string tool, string[] arguments, CancellationToken ct, bool capture)
    {
        var start = new ProcessStartInfo { FileName = tool };
        // ArgumentList passes each value as one argument; no shell is involved.
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        // ufw / firewall-cmd print English only under a C locale, and that is what ufw's status is parsed in.
        start.Environment["LC_ALL"] = "C";
        return RunAsync(start, ct, capture);
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(ProcessStartInfo start, CancellationToken ct, bool capture)
    {
        start.CreateNoWindow = true;
        start.UseShellExecute = false;
        // Always drained: a tool blocks once an unread pipe fills up.
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{Path.GetFileName(start.FileName)} did not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(output, errors);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch (InvalidOperationException) { /* already gone */ }
            throw;
        }
        return (process.ExitCode, capture ? output.Result : "");
    }
}
