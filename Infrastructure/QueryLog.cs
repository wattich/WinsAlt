using WinsAlt.Core.Domain;

namespace WinsAlt.Infrastructure;

public enum LogOp : byte { Query, Register, Refresh, Release }

public enum LogResult : byte
{
    Hit,
    StaticHit,
    DnsHit,
    PartnerHit,
    ReplicaHit,
    NodeStatus,
    Miss,
    Registered,
    Refreshed,
    Released,
    Challenging,
    Conflict,
    Denied,
    NotOwner,
    Refused,
    Blocked,
    Ignored
}

/// <summary>One row of the dashboard's query log.</summary>
public sealed record QueryLogDto(long T, string Op, string Result, string Name, string Suffix, string Client, string? Answer);

/// /// <summary>
/// Ring buffer of recent protocol transactions for the dashboard.
///
/// Recording is lock-free, so the workers never queue behind each other (or behind the dashboard)
/// to log a packet: a writer claims a slot with one <see cref="Interlocked.Increment(ref long)"/>,
/// fills it, then publishes the slot's sequence number. A reader takes an entry only if that
/// sequence is the same before and after it copied the slot - an entry caught mid-write, or
/// overwritten meanwhile, is simply skipped. Entries are fixed-size structs in a preallocated
/// array; strings are built only when the dashboard asks for a snapshot.
/// </summary>
public sealed class QueryLog
{
    private struct Entry
    {
        public long Sequence;   // 0 = empty or being written; otherwise 1 + the claim number
        public long TimeMs;
        public NameKey Name;
        public uint Client;
        public uint Answer;
        public LogOp Op;
        public LogResult Result;
    }

    private readonly Entry[] _ring;
    private long _claimed;

    public QueryLog(WinsOptions options) => _ring = new Entry[options.QueryLogCapacity];

    public void Add(LogOp op, LogResult result, NameKey name, uint client, uint answer = 0)
    {
        long claim = Interlocked.Increment(ref _claimed) - 1;
        ref Entry e = ref _ring[(int)(claim % _ring.Length)];

        Volatile.Write(ref e.Sequence, 0);          // "being written" - readers skip it
        e.TimeMs = Clock.UnixNowMs();
        e.Name = name;
        e.Client = client;
        e.Answer = answer;
        e.Op = op;
        e.Result = result;
        Volatile.Write(ref e.Sequence, claim + 1);  // publish
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<QueryLogDto> Snapshot(int limit)
    {
        long claimed = Volatile.Read(ref _claimed);
        int take = (int)Math.Min(Math.Min(claimed, _ring.Length), Math.Max(limit, 0));

        var result = new List<QueryLogDto>(take);
        for (int i = 0; i < take; i++)
        {
            long claim = claimed - 1 - i;
            ref Entry slot = ref _ring[(int)(claim % _ring.Length)];

            long before = Volatile.Read(ref slot.Sequence);
            Entry e = slot;
            if (before != claim + 1 || Volatile.Read(ref slot.Sequence) != before) continue;

            result.Add(new QueryLogDto(
                e.TimeMs,
                e.Op.ToString(),
                e.Result.ToString(),
                e.Name.ToDisplayName(),
                e.Name.Suffix.ToString("X2"),
                Ipv4.ToString(e.Client),
                e.Answer == 0 ? null : Ipv4.ToString(e.Answer)));
        }
        return result;
    }
}