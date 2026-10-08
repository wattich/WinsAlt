namespace WinsAlt.Core.Domain;

public enum NameKind : byte
{
    /// <summary>One owner (one address).</summary>
    Unique,
    /// <summary>Shared name: any number of nodes may register it.</summary>
    Group,
    /// <summary>One multi-homed owner registering several of its own addresses.</summary>
    Multihomed
}

/// <summary>One address registered under a name, with its own expiry (unix seconds, UTC).</summary>
public readonly record struct NameMember(uint Address, long ExpiresAt)
{
    /// <summary>Expiry used for static mappings - they never age out.</summary>
    public const long Never = long.MaxValue;
}

/// <summary>
/// A name in the database. Readers (the query hot path) take the <see cref="Members"/> array
/// reference once and iterate it without locking. Writers run under the store's write lock and
/// either swap in a new array (membership change) or overwrite one element's expiry in place
/// (refresh - the address half of the element is rewritten with the same value, so a concurrent
/// reader can only ever observe the old or the new expiry).
/// </summary>
public sealed class NameRecord
{
    private volatile NameMember[] _members;

    public NameRecord(NameKey key, NameKind kind, ushort nbFlags, bool isStatic, long registeredAt,
        NameMember[] members, string? comment = null)
    {
        Key = key;
        Kind = kind;
        NbFlags = nbFlags;
        IsStatic = isStatic;
        RegisteredAt = registeredAt;
        Comment = comment;
        _members = members;
    }

    public NameKey Key { get; }
    public NameKind Kind { get; internal set; }
    /// <summary>NB_FLAGS as registered (group bit + owner node type).</summary>
    public ushort NbFlags { get; internal set; }
    public bool IsStatic { get; }
    public long RegisteredAt { get; }
    public string? Comment { get; }

    public NameMember[] Members
    {
        get => _members;
        internal set => _members = value;
    }

    public bool IsActive(long now)
    {
        foreach (var m in _members)
            if (m.ExpiresAt > now) return true;
        return false;
    }
}
