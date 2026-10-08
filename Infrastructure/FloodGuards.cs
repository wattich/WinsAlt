using System.Runtime.CompilerServices;

namespace WinsAlt.Infrastructure;

/// <summary>
/// Per-source-address token bucket for the UDP receive loop.
///
/// NBNS has no handshake, so a flood (or a reflection attack using spoofed sources) costs the
/// sender one datagram per request. This caps what any one address can make the server do: each
/// address gets <c>burst</c> requests at once and <c>perSecond</c> sustained; the rest are dropped
/// before they are even queued.
///
/// Fixed memory, no allocation, no lock: a flat table of buckets indexed by a hash of the
/// address, probed over a few neighbouring slots. It is only ever touched by the single receive
/// loop. When every probed slot belongs to another address, the least recently used one is
/// reassigned - under a many-source flood a bucket can be recycled early, which only ever errs
/// toward letting a packet through, never toward blocking an innocent address.
/// </summary>
public sealed class SourceRateLimiter
{
    private struct Bucket
    {
        public uint Address;
        public long LastMs;      // 0 = never used
        public long MilliTokens; // tokens x 1000, so refill needs no floating point
    }

    private const int Slots = 8192;       // power of two
    private const int Probes = 4;
    private readonly Bucket[] _buckets = new Bucket[Slots];
    private readonly long _perSecond;
    private readonly long _burstMilli;

    public SourceRateLimiter(int perSecond, int burst)
    {
        _perSecond = perSecond;
        _burstMilli = Math.Max(burst, perSecond) * 1000L;
    }

    /// <summary>Takes one token for <paramref name="address"/>. False = over the limit, drop the packet.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Allow(uint address, long nowMs)
    {
        int start = (int)((address * 2654435761u) >> 19); // Fibonacci hash -> 13 bits
        int victim = start;
        long oldest = long.MaxValue;

        for (int i = 0; i < Probes; i++)
        {
            int index = (start + i) & (Slots - 1);
            ref Bucket b = ref _buckets[index];

            if (b.Address == address && b.LastMs != 0)
            {
                // 1 token = 1000 milli-tokens; perSecond tokens/s = perSecond milli-tokens/ms.
                long refilled = b.MilliTokens + (nowMs - b.LastMs) * _perSecond;
                b.MilliTokens = refilled > _burstMilli ? _burstMilli : refilled;
                b.LastMs = nowMs;
                if (b.MilliTokens < 1000) return false;
                b.MilliTokens -= 1000;
                return true;
            }

            if (b.LastMs < oldest)
            {
                oldest = b.LastMs;
                victim = index;
            }
        }

        ref Bucket fresh = ref _buckets[victim];
        fresh.Address = address;
        fresh.LastMs = nowMs;
        fresh.MilliTokens = _burstMilli - 1000;
        return true;
    }
}

/// <summary>
/// Lets at most N events per second through - for log lines written per packet. A registration
/// storm must not become a storm of Windows Event Log writes; counters and the query log still
/// record every event, only the prose is thinned.
/// </summary>
public sealed class LogGate
{
    private readonly int _perSecond;
    private long _window;
    private int _used;

    public LogGate(int perSecond) => _perSecond = perSecond;

    public bool Allow()
    {
        long second = Environment.TickCount64 / 1000;
        if (Volatile.Read(ref _window) != second)
        {
            Volatile.Write(ref _window, second);
            Volatile.Write(ref _used, 0);
        }
        return Interlocked.Increment(ref _used) <= _perSecond;
    }
}
