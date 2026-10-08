using WinsAlt.Core.Domain;
using WinsAlt.Infrastructure;

namespace WinsAlt.Network;

/// <summary>
/// Background housekeeping for the name database:
///  - expiry sweep: drops registrations whose TTL ran out without a refresh (queries already
///    ignore an expired entry the instant it expires - the sweep only reclaims the memory and
///    reports the event);
///  - persistence: snapshots the database when it changed, and once more on shutdown.
/// </summary>
public sealed class NameMaintenanceService : BackgroundService
{
    private readonly NameStore _store;
    private readonly WinsDatabase _database;
    private readonly DnsFallbackResolver _dns;
    private readonly PartnerService _partners;
    private readonly WinsCounters _counters;
    private readonly WinsOptions _options;
    private readonly ILogger<NameMaintenanceService> _logger;

    public NameMaintenanceService(NameStore store, WinsDatabase database, DnsFallbackResolver dns, PartnerService partners,
        WinsCounters counters, WinsOptions options, ILogger<NameMaintenanceService> logger)
    {
        _store = store;
        _database = database;
        _dns = dns;
        _partners = partners;
        _counters = counters;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sweepEvery = TimeSpan.FromSeconds(_options.SweepIntervalSeconds);
        var persistEvery = TimeSpan.FromSeconds(_options.PersistIntervalSeconds);
        var nextSweep = DateTime.UtcNow + sweepEvery;
        var nextPersist = DateTime.UtcNow + persistEvery;

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = DateTime.UtcNow;
                if (now >= nextSweep)
                {
                    nextSweep = now + sweepEvery;
                    Sweep();
                }
                if (now >= nextPersist)
                {
                    nextPersist = now + persistEvery;
                    _database.SaveIfDirty();
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            // Final snapshot so a clean stop/restart keeps every live registration.
            _database.SaveIfDirty();
        }
    }

    private void Sweep()
    {
        try
        {
            long now = Clock.UnixNow();
            var expired = _store.SweepExpired(now);
            foreach (var name in expired)
            {
                WinsCounters.Inc(ref _counters.Expired);
                _logger.LogInformation("Expired {Name} (not refreshed within its TTL)", name);
            }
            _dns.Prune(now);
            _partners.Prune(now);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Expiry sweep failed");
        }
    }
}
