using WinsAlt.Core.Domain;
using WinsAlt.Core.Protocol;

namespace WinsAlt.Infrastructure;

/// <summary>A name owned by another WinsAlt server, as received through replication.</summary>
public sealed record ReplicaRecord(NameKey Key, NameKind Kind, ushort NbFlags, bool IsStatic, long RegisteredAt,
    NameMember[] Members, string Origin)
{
    public long LatestExpiry
    {
        get
        {
            long latest = 0;
            foreach (var m in Members)
                if (m.ExpiresAt > latest) latest = m.ExpiresAt;
            return latest;
        }
    }

    public bool IsActive(long now) => LatestExpiry > now;
}

/// <summary>
/// Read-only copies of the names registered on the other WinsAlt servers.
///
/// Each replication peer contributes one snapshot (everything that peer owns). The snapshots are
/// merged into a single immutable dictionary that is swapped in atomically, so the query path
/// reads it with one lookup, no lock and no allocation.
///
/// Replicas never mix into the local database: a name registered on this server always wins
/// over a replica of the same name, and a replica can never be refreshed, released or expired
/// from here - only its owner does that, and the change arrives with the next snapshot.
/// </summary>
public sealed class ReplicaStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, IReadOnlyList<ReplicaRecord>> _byPeer = new(StringComparer.Ordinal);
    private volatile Dictionary<NameKey, ReplicaRecord> _merged = new();

    public int Count => _merged.Count;

    public IEnumerable<ReplicaRecord> Records => _merged.Values;

    /// <summary>Hot path: same contract as <see cref="NameStore.Query"/>. Never allocates, never locks.</summary>
    public bool Query(NameKey key, long now, uint staticTtl, Span<uint> addresses, out int count, out ushort nbFlags, out uint ttl)
    {
        count = 0;
        nbFlags = 0;
        ttl = 0;
        if (!_merged.TryGetValue(key, out var record)) return false;

        long latest = 0;
        foreach (var m in record.Members)
        {
            if (m.ExpiresAt <= now) continue;
            if (m.ExpiresAt > latest) latest = m.ExpiresAt;
            if (count < addresses.Length) addresses[count++] = m.Address;
        }
        if (count == 0) return false;

        nbFlags = record.NbFlags;
        if (record.Kind == NameKind.Group)
        {
            nbFlags |= NbFlags.Group;
            if (key.Suffix != 0x1C)
            {
                addresses[0] = Ipv4.LimitedBroadcast;
                count = 1;
            }
        }

        ttl = record.IsStatic ? staticTtl : (uint)Math.Min(latest - now, uint.MaxValue);
        return true;
    }

    /// <summary>
    /// Adds the replicated members of an internet group (suffix 1C - domain controllers) to an
    /// answer that already holds this server's own members, so clients see the DCs of every site.
    /// </summary>
    public int AppendMembers(NameKey key, long now, Span<uint> addresses, int count)
    {
        if (!_merged.TryGetValue(key, out var record) || record.Kind != NameKind.Group) return count;

        foreach (var m in record.Members)
        {
            if (count >= addresses.Length) break;
            if (m.ExpiresAt <= now || addresses[..count].Contains(m.Address)) continue;
            addresses[count++] = m.Address;
        }
        return count;
    }

    public bool TryGet(NameKey key, out ReplicaRecord record) => _merged.TryGetValue(key, out record!);

    /// <summary>True when another WinsAlt server holds <paramref name="key"/> as a static mapping.</summary>
    public bool IsStatic(NameKey key, long now) => _merged.TryGetValue(key, out var record) && record.IsStatic && record.IsActive(now);

    /// <summary>
    /// True when another server owns <paramref name="key"/> as a non-group name at an address
    /// other than <paramref name="address"/> - i.e. registering it here would take it away from
    /// its owner. (A replica that already lists the address is the same machine moving servers.)
    /// </summary>
    public bool TryGetForeignOwner(NameKey key, long now, uint address, out uint owner)
    {
        owner = 0;
        if (!_merged.TryGetValue(key, out var record) || record.Kind == NameKind.Group) return false;

        foreach (var m in record.Members)
        {
            if (m.ExpiresAt <= now) continue;
            if (m.Address == address) return false;
            if (owner == 0) owner = m.Address;
        }
        return owner != 0;
    }

    /// <summary>Replaces everything previously received from <paramref name="peer"/>.</summary>
    public void SetPeer(string peer, IReadOnlyList<ReplicaRecord> records)
    {
        lock (_lock)
        {
            _byPeer[peer] = records;
            Rebuild_NoLock();
        }
    }

    public void RemovePeer(string peer)
    {
        lock (_lock)
        {
            if (_byPeer.Remove(peer)) Rebuild_NoLock();
        }
    }

    /// <summary>Forgets every peer that is not in <paramref name="peers"/> (removed or paused ones).</summary>
    public void KeepOnly(IReadOnlyCollection<string> peers)
    {
        lock (_lock)
        {
            var stale = _byPeer.Keys.Where(k => !peers.Contains(k)).ToList();
            if (stale.Count == 0) return;
            foreach (var key in stale) _byPeer.Remove(key);
            Rebuild_NoLock();
        }
    }

    private void Rebuild_NoLock()
    {
        var merged = new Dictionary<NameKey, ReplicaRecord>();
        foreach (var records in _byPeer.Values)
        {
            foreach (var record in records)
            {
                if (!merged.TryGetValue(record.Key, out var existing))
                {
                    merged[record.Key] = record;
                    continue;
                }

                // The same name on two peers. A group simply has members at both sites; for
                // anything else keep one owner: a static mapping first, then whichever
                // registration is the most recently refreshed.
                if (existing.Kind == NameKind.Group && record.Kind == NameKind.Group)
                    merged[record.Key] = MergeGroups(existing, record);
                else if (Preferred(record, existing))
                    merged[record.Key] = record;
            }
        }
        _merged = merged;
    }

    private static bool Preferred(ReplicaRecord candidate, ReplicaRecord current)
    {
        if (candidate.IsStatic != current.IsStatic) return candidate.IsStatic;
        return candidate.LatestExpiry > current.LatestExpiry;
    }

    private static ReplicaRecord MergeGroups(ReplicaRecord a, ReplicaRecord b)
    {
        var members = new List<NameMember>(a.Members);
        foreach (var m in b.Members)
            if (members.Count < NbnsPackets.MaxAddresses && !members.Exists(x => x.Address == m.Address)) members.Add(m);

        string origin = a.Origin.Contains(b.Origin, StringComparison.Ordinal) ? a.Origin : $"{a.Origin}, {b.Origin}";
        return a with { Members = members.ToArray(), Origin = origin, IsStatic = a.IsStatic || b.IsStatic };
    }
}
