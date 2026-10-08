using System.Collections.Concurrent;
using WinsAlt.Core.Domain;
using WinsAlt.Core.Protocol;

namespace WinsAlt.Infrastructure;

public enum QueryOutcome : byte { Miss, Hit, StaticHit }

public enum RegisterOutcome : byte
{
    Registered,
    Refreshed,
    /// <summary>A static mapping already covers this name/address - acknowledged, nothing stored.</summary>
    StaticMatch,
    /// <summary>Refused outright (static mapping with another address, or unique/group type clash).</summary>
    Denied,
    /// <summary>The database, or this address, already holds as many dynamic names as policy allows.</summary>
    QuotaExceeded,
    /// <summary>A unique name is held by a different address - the caller applies the conflict policy.</summary>
    Conflict
}

public enum ReleaseOutcome : byte { Released, NotFound, NotOwner, Static }

/// <summary>A static mapping resolved to database form (see StaticMappingService).</summary>
public readonly record struct StaticEntry(NameKey Key, NameKind Kind, uint[] Addresses, string? Comment);

/// <summary>
/// The in-memory name database.
///
/// Reads (name queries - by far the dominant traffic) are lock-free: one ConcurrentDictionary
/// lookup keyed by the two-ulong <see cref="NameKey"/>, then a walk over an immutable-by-convention
/// member array. Nothing on that path allocates.
///
/// Writes (register / refresh / release / expiry / static reload) are rare by comparison - a
/// client touches its names once per renewal interval - so they all serialize on one lock. That
/// keeps every multi-step transition (check owner → swap members → remove) trivially atomic.
/// </summary>
public sealed class NameStore
{
    private readonly ConcurrentDictionary<NameKey, NameRecord> _records = new();
    private readonly Lock _writeLock = new();
    // Flood guards (see WinsOptions): a cap on dynamic names overall and per registered address.
    // Both tallies are only touched under _writeLock, by the three *_NoLock helpers at the bottom.
    private readonly Dictionary<uint, int> _namesPerAddress = new();
    // Read from the options each time: both limits can be changed from the dashboard while running.
    private readonly WinsOptions _options;
    private int _dynamicNames;
    private long _version;
    private int _staticCount;

    public NameStore(WinsOptions options) => _options = options;

    /// <summary>Bumped on every mutation of dynamic data; the persistence layer saves when it moves.</summary>
    public long Version => Volatile.Read(ref _version);

    public int Count => _records.Count;
    public int StaticCount => Volatile.Read(ref _staticCount);

    public bool TryGet(NameKey key, out NameRecord record) => _records.TryGetValue(key, out record!);

    public bool HasActive(NameKey key, long now) => _records.TryGetValue(key, out var record) && record.IsActive(now);

    public bool IsStaticName(NameKey key) => _records.TryGetValue(key, out var record) && record.IsStatic;

    /// <summary>True when a static mapping gives <paramref name="key"/> exactly this address.</summary>
    public bool IsStaticFor(NameKey key, uint address, long now) =>
        _records.TryGetValue(key, out var record) && record.IsStatic && IndexOfActive(record.Members, address, now) >= 0;

    /// <summary>
    /// Hot path. Copies the live addresses of <paramref name="key"/> into <paramref name="addresses"/>
    /// and reports the flags/TTL to answer with. Never allocates, never locks.
    /// </summary>
    public QueryOutcome Query(NameKey key, long now, uint staticTtl, Span<uint> addresses,
        out int count, out ushort nbFlags, out uint ttl)
    {
        count = 0;
        nbFlags = 0;
        ttl = 0;
        if (!_records.TryGetValue(key, out var record)) return QueryOutcome.Miss;

        var members = record.Members;
        long latest = 0;
        foreach (var m in members)
        {
            if (m.ExpiresAt <= now) continue;
            if (m.ExpiresAt > latest) latest = m.ExpiresAt;
            if (count < addresses.Length) addresses[count++] = m.Address;
        }
        if (count == 0) return QueryOutcome.Miss;

        nbFlags = record.NbFlags;
        if (record.Kind == NameKind.Group)
        {
            nbFlags |= NbFlags.Group;
            // WINS semantics: a normal group resolves to the limited broadcast address; only an
            // "internet group" (suffix 0x1C - domain controllers) returns its member list.
            if (key.Suffix != 0x1C)
            {
                addresses[0] = Ipv4.LimitedBroadcast;
                count = 1;
            }
        }

        ttl = record.IsStatic ? staticTtl : (uint)Math.Min(latest - now, uint.MaxValue);
        return record.IsStatic ? QueryOutcome.StaticHit : QueryOutcome.Hit;
    }

    public RegisterOutcome Register(NameKey key, uint address, ushort nbFlags, NameKind kind, long now, uint ttl,
        out uint conflictAddress)
    {
        conflictAddress = 0;
        long expires = now + ttl;

        lock (_writeLock)
        {
            if (!_records.TryGetValue(key, out var record) || !record.IsActive(now))
            {
                if (OverQuota_NoLock(address, newName: record is null)) return RegisterOutcome.QuotaExceeded;
                Put_NoLock(new NameRecord(key, kind, nbFlags, isStatic: false, now, [new NameMember(address, expires)]));
                Touch();
                return RegisterOutcome.Registered;
            }

            var members = record.Members;
            int index = IndexOfActive(members, address, now);
            bool wantsGroup = kind == NameKind.Group;
            bool isGroup = record.Kind == NameKind.Group;

            if (record.IsStatic)
                return index >= 0 || (wantsGroup && isGroup) ? RegisterOutcome.StaticMatch : RegisterOutcome.Denied;

            if (wantsGroup != isGroup)
            {
                // Unique <-> group is only allowed when the requester is the name's sole owner.
                if (index < 0 || CountActive(members, now) != 1) return RegisterOutcome.Denied;
                Put_NoLock(new NameRecord(key, kind, nbFlags, isStatic: false, now, [new NameMember(address, expires)]));
                Touch();
                return RegisterOutcome.Registered;
            }

            if (index >= 0)
            {
                members[index] = new NameMember(address, expires);
                record.NbFlags = nbFlags;
                if (kind == NameKind.Multihomed) record.Kind = NameKind.Multihomed;
                Touch();
                return RegisterOutcome.Refreshed;
            }

            if (isGroup || (kind == NameKind.Multihomed && record.Kind == NameKind.Multihomed))
            {
                if (OverQuota_NoLock(address, newName: false)) return RegisterOutcome.QuotaExceeded;
                SetMembers_NoLock(record, AddMember(members, address, expires, now));
                Touch();
                return RegisterOutcome.Registered;
            }

            conflictAddress = FirstActive(members, now);
            return RegisterOutcome.Conflict;
        }
    }

    /// <summary>
    /// Replaces whatever dynamic record holds <paramref name="key"/> - used once a conflict has
    /// been decided in the newcomer's favour. A static mapping is never replaced.
    /// </summary>
    public bool ForceRegister(NameKey key, uint address, ushort nbFlags, NameKind kind, long now, uint ttl)
    {
        lock (_writeLock)
        {
            if (_records.TryGetValue(key, out var record) && record.IsStatic) return false;
            if (OverQuota_NoLock(address, newName: record is null)) return false;
            Put_NoLock(new NameRecord(key, kind, nbFlags, isStatic: false, now, [new NameMember(address, now + ttl)]));
            Touch();
            return true;
        }
    }

    /// <summary>
    /// Adds another address of the SAME host to a name it already owns (a machine on both LAN and
    /// Wi-Fi), turning the record multihomed. Falls back to a plain registration if the record
    /// vanished meanwhile. A static or group record is never changed.
    /// </summary>
    public bool AddOwnAddress(NameKey key, uint address, ushort nbFlags, long now, uint ttl)
    {
        lock (_writeLock)
        {
            if (!_records.TryGetValue(key, out var record) || !record.IsActive(now))
            {
                if (OverQuota_NoLock(address, newName: record is null)) return false;
                Put_NoLock(new NameRecord(key, NameKind.Unique, nbFlags, isStatic: false, now, [new NameMember(address, now + ttl)]));
                Touch();
                return true;
            }
            if (record.IsStatic || record.Kind == NameKind.Group) return false;
            if (OverQuota_NoLock(address, newName: false)) return false;

            SetMembers_NoLock(record, AddMember(record.Members, address, now + ttl, now));
            record.Kind = NameKind.Multihomed;
            Touch();
            return true;
        }
    }

    public ReleaseOutcome Release(NameKey key, uint address, long now)
    {
        lock (_writeLock)
        {
            if (!_records.TryGetValue(key, out var record) || !record.IsActive(now)) return ReleaseOutcome.NotFound;
            if (record.IsStatic) return ReleaseOutcome.Static;

            var members = record.Members;
            int index = IndexOfActive(members, address, now);
            if (index < 0) return ReleaseOutcome.NotOwner;

            var remaining = Without(members, index, now);
            if (remaining.Length == 0) Drop_NoLock(key);
            else SetMembers_NoLock(record, remaining);
            Touch();
            return ReleaseOutcome.Released;
        }
    }

    /// <summary>Removes one dynamic record regardless of owner (dashboard "delete").</summary>
    public bool Remove(NameKey key)
    {
        lock (_writeLock)
        {
            if (!_records.TryGetValue(key, out var record) || record.IsStatic) return false;
            Drop_NoLock(key);
            Touch();
            return true;
        }
    }

    /// <summary>Drops expired members and the records left empty. Returns the names fully removed.</summary>
    public List<NameKey> SweepExpired(long now)
    {
        var removed = new List<NameKey>();
        foreach (var (key, record) in _records)
        {
            if (record.IsStatic || !HasExpired(record.Members, now)) continue;

            lock (_writeLock)
            {
                // Re-read under the lock: a refresh may have landed since the unlocked check.
                if (!_records.TryGetValue(key, out var current) || !ReferenceEquals(current, record)) continue;

                var live = Without(record.Members, -1, now);
                if (live.Length == record.Members.Length) continue;

                if (live.Length == 0)
                {
                    Drop_NoLock(key);
                    removed.Add(key);
                }
                else SetMembers_NoLock(record, live);
                Touch();
            }
        }
        return removed;
    }

    /// <summary>
    /// Makes the set of static records equal to <paramref name="entries"/>. New entries are put
    /// in place before stale ones are removed, so a name that stays static never has a gap in
    /// which a query could miss. A static mapping overrides any dynamic record of the same name.
    /// </summary>
    public void ReplaceStatics(IReadOnlyList<StaticEntry> entries, long now)
    {
        lock (_writeLock)
        {
            var wanted = new HashSet<NameKey>(entries.Count);
            foreach (var e in entries)
            {
                var members = new NameMember[Math.Min(e.Addresses.Length, NbnsPackets.MaxAddresses)];
                for (int i = 0; i < members.Length; i++) members[i] = new NameMember(e.Addresses[i], NameMember.Never);

                ushort flags = e.Kind == NameKind.Group ? NbFlags.Group : (ushort)0;
                Put_NoLock(new NameRecord(e.Key, e.Kind, flags, isStatic: true, now, members, e.Comment));
                wanted.Add(e.Key);
            }

            foreach (var (key, record) in _records)
                if (record.IsStatic && !wanted.Contains(key)) Drop_NoLock(key);

            Volatile.Write(ref _staticCount, wanted.Count);
            Touch();
        }
    }

    /// <summary>Loads persisted dynamic records at startup (existing names are kept).</summary>
    public int Restore(IEnumerable<NameRecord> records)
    {
        int restored = 0;
        lock (_writeLock)
        {
            foreach (var record in records)
            {
                if (_records.ContainsKey(record.Key)) continue;
                Put_NoLock(record);
                restored++;
            }
        }
        return restored;
    }

    /// <summary>Weakly-consistent enumeration for the API and the persistence snapshot.</summary>
    public IEnumerable<NameRecord> Records => _records.Values;

    private void Touch() => Interlocked.Increment(ref _version);

    // ---- the only three places that add, replace or remove what a record holds (keeps the quota tallies exact) ----

    private void Put_NoLock(NameRecord record)
    {
        if (_records.TryGetValue(record.Key, out var old)) Tally_NoLock(old, -1);
        _records[record.Key] = record;
        Tally_NoLock(record, +1);
    }

    private void Drop_NoLock(NameKey key)
    {
        if (_records.TryRemove(key, out var old)) Tally_NoLock(old, -1);
    }

    private void SetMembers_NoLock(NameRecord record, NameMember[] members)
    {
        Tally_NoLock(record, -1);
        record.Members = members;
        Tally_NoLock(record, +1);
    }

    private void Tally_NoLock(NameRecord record, int delta)
    {
        if (record.IsStatic) return;
        _dynamicNames += delta;
        foreach (var m in record.Members)
        {
            int count = _namesPerAddress.GetValueOrDefault(m.Address) + delta;
            if (count > 0) _namesPerAddress[m.Address] = count;
            else _namesPerAddress.Remove(m.Address);
        }
    }

    /// <summary>Would one more dynamic name for <paramref name="address"/> break a cap? (0 = that cap is off.)</summary>
    private bool OverQuota_NoLock(uint address, bool newName) =>
        (newName && _options.MaxDynamicNames > 0 && _dynamicNames >= _options.MaxDynamicNames)
        || (_options.MaxNamesPerAddress > 0 && _namesPerAddress.GetValueOrDefault(address) >= _options.MaxNamesPerAddress);

    private static int IndexOfActive(NameMember[] members, uint address, long now)
    {
        for (int i = 0; i < members.Length; i++)
            if (members[i].Address == address && members[i].ExpiresAt > now) return i;
        return -1;
    }

    private static int CountActive(NameMember[] members, long now)
    {
        int n = 0;
        foreach (var m in members)
            if (m.ExpiresAt > now) n++;
        return n;
    }

    private static uint FirstActive(NameMember[] members, long now)
    {
        foreach (var m in members)
            if (m.ExpiresAt > now) return m.Address;
        return 0;
    }

    private static bool HasExpired(NameMember[] members, long now)
    {
        foreach (var m in members)
            if (m.ExpiresAt <= now) return true;
        return false;
    }

    /// <summary>Live members except the one at <paramref name="skipIndex"/> (-1 = keep all live ones).</summary>
    private static NameMember[] Without(NameMember[] members, int skipIndex, long now)
    {
        var list = new List<NameMember>(members.Length);
        for (int i = 0; i < members.Length; i++)
            if (i != skipIndex && members[i].ExpiresAt > now) list.Add(members[i]);
        return list.ToArray();
    }

    private static NameMember[] AddMember(NameMember[] members, uint address, long expires, long now)
    {
        var list = new List<NameMember>(members.Length + 1);
        foreach (var m in members)
            if (m.ExpiresAt > now && m.Address != address) list.Add(m);

        // Full group: evict the member closest to expiring.
        while (list.Count >= NbnsPackets.MaxAddresses)
        {
            int oldest = 0;
            for (int i = 1; i < list.Count; i++)
                if (list[i].ExpiresAt < list[oldest].ExpiresAt) oldest = i;
            list.RemoveAt(oldest);
        }

        list.Add(new NameMember(address, expires));
        return list.ToArray();
    }
}
