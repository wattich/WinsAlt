using System.Globalization;
using WinsAlt.Core.Domain;
using WinsAlt.Core.Protocol;
using WinsAlt.Infrastructure;
using WinsAlt.Network;

namespace WinsAlt;

/// <summary>
/// CLI interactive mode: when the exe is started from a terminal (not by the Service Control
/// Manager), a small command prompt runs next to the server for development and debugging.
/// Type <c>help</c> for the command list.
/// </summary>
public sealed class ConsoleCommandService : BackgroundService
{
    private readonly NbnsServer _server;
    private readonly NameStore _store;
    private readonly DnsFallbackResolver _dns;
    private readonly WinsCounters _counters;
    private readonly WinsOptions _options;
    private readonly IHostApplicationLifetime _lifetime;

    public ConsoleCommandService(NbnsServer server, NameStore store, DnsFallbackResolver dns, WinsCounters counters,
        WinsOptions options, IHostApplicationLifetime lifetime)
    {
        _server = server;
        _store = store;
        _dns = dns;
        _counters = counters;
        _options = options;
        _lifetime = lifetime;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Console.ReadLine blocks and cannot be cancelled, so it gets its own background thread
        // (which dies with the process) instead of tying up a thread-pool thread.
        var thread = new Thread(() => ReadLoop(stoppingToken)) { IsBackground = true, Name = "console-commands" };
        thread.Start();
        return Task.CompletedTask;
    }

    private void ReadLoop(CancellationToken ct)
    {
        Console.WriteLine("WinsAlt interactive mode - type 'help' for commands, 'quit' (or Ctrl+C) to stop.");

        while (!ct.IsCancellationRequested)
        {
            string? line;
            try { line = Console.ReadLine(); }
            catch (IOException) { return; }
            if (line is null) return; // stdin closed

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) continue;

            try
            {
                switch (parts[0].ToLowerInvariant())
                {
                    case "help" or "?":
                        Console.WriteLine("  status                 listener state and counters");
                        Console.WriteLine("  names [filter]         list registered names");
                        Console.WriteLine("  resolve <name> [hex]   resolve like a client would (suffix default 20)");
                        Console.WriteLine("  quit                   stop the server");
                        break;
                    case "status":
                        PrintStatus();
                        break;
                    case "names":
                        PrintNames(parts.Length > 1 ? parts[1] : "");
                        break;
                    case "resolve" when parts.Length > 1:
                        Resolve(parts[1], parts.Length > 2 ? parts[2] : "20", ct);
                        break;
                    case "quit" or "exit":
                        _lifetime.StopApplication();
                        return;
                    default:
                        Console.WriteLine("Unknown command - type 'help'.");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Command failed: {ex.Message}");
            }
        }
    }

    private void PrintStatus()
    {
        Console.WriteLine($"  Listener   {_server.State} on UDP {_server.EndPoint}" + (_server.LastError is { } e ? $" - {e}" : ""));
        Console.WriteLine($"  Names      {_store.Count} ({_store.StaticCount} static)");
        Console.WriteLine($"  Packets    {WinsCounters.Read(ref _counters.PacketsReceived)} received, " +
                          $"{WinsCounters.Read(ref _counters.ResponsesSent)} answered, {WinsCounters.Read(ref _counters.Dropped)} dropped");
        Console.WriteLine($"  Queries    {WinsCounters.Read(ref _counters.Queries)} " +
                          $"(hit {WinsCounters.Read(ref _counters.QueryHits)}, dns {WinsCounters.Read(ref _counters.DnsHits)}, " +
                          $"miss {WinsCounters.Read(ref _counters.QueryMisses)})");
        Console.WriteLine($"  Register   {WinsCounters.Read(ref _counters.Registrations)} new, " +
                          $"{WinsCounters.Read(ref _counters.Refreshes)} refreshed, {WinsCounters.Read(ref _counters.Releases)} released, " +
                          $"{WinsCounters.Read(ref _counters.Conflicts)} conflicts");
    }

    private void PrintNames(string filter)
    {
        long now = Clock.UnixNow();
        int shown = 0;
        foreach (var record in _store.Records.OrderBy(r => r.Key.ToDisplayName(), StringComparer.Ordinal))
        {
            if (!record.IsActive(now)) continue;
            string name = record.Key.ToString();
            if (filter.Length > 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

            var addresses = record.Members.Where(m => m.ExpiresAt > now).Select(m => Ipv4.ToString(m.Address));
            string kind = record.IsStatic ? "static" : record.Kind.ToString().ToLowerInvariant();
            Console.WriteLine($"  {name,-20} {kind,-11} {string.Join(", ", addresses)}");
            shown++;
        }
        Console.WriteLine($"  {shown} name(s)");
    }

    private void Resolve(string name, string suffixHex, CancellationToken ct)
    {
        if (!byte.TryParse(suffixHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte suffix)
            || !NameKey.TryCreate(name, suffix, out var key))
        {
            Console.WriteLine("  Usage: resolve <name> [suffix-hex]");
            return;
        }

        var addresses = new uint[NbnsPackets.MaxAddresses];
        var outcome = _store.Query(key, Clock.UnixNow(), _options.MaxTtlSeconds, addresses, out int count, out _, out _);
        string source = outcome == QueryOutcome.StaticHit ? "static" : "registration";

        if (outcome == QueryOutcome.Miss && _dns.Enabled && DnsFallbackResolver.IsEligible(key))
        {
            addresses[0] = _dns.ResolveAsync(key, ct).GetAwaiter().GetResult();
            count = addresses[0] != 0 ? 1 : 0;
            source = "dns";
        }

        Console.WriteLine(count == 0
            ? $"  {key} not found"
            : $"  {key} -> {string.Join(", ", addresses.Take(count).Select(a => Ipv4.ToString(a)))} ({source})");
    }
}
