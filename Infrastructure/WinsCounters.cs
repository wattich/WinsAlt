using System.Runtime.CompilerServices;

namespace WinsAlt.Infrastructure;

/// <summary>
/// Process-lifetime protocol counters. Public fields so the packet path can bump one with a
/// single <see cref="Interlocked.Increment(ref long)"/> - no lock, no allocation.
/// </summary>
public sealed class WinsCounters
{
    public long PacketsReceived;
    public long ResponsesSent;
    public long Queries;
    public long QueryHits;
    public long QueryMisses;
    public long DnsHits;
    public long NodeStatusReplies;
    public long PartnerHits;
    public long ReplicaHits;
    public long Registrations;
    public long Refreshes;
    public long Releases;
    public long Conflicts;
    public long RefusedByPolicy;
    public long BlockedNames;
    public long RateLimited;
    public long Oversized;
    public long Expired;
    public long Malformed;
    public long Dropped;
    public long IgnoredBroadcasts;
    public long Unsupported;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Inc(ref long counter) => Interlocked.Increment(ref counter);

    public static long Read(ref long counter) => Interlocked.Read(ref counter);
}
