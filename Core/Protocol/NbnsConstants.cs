namespace WinsAlt.Core.Protocol;

/// <summary>NBNS OPCODE field (RFC 1002 §4.2.1.1).</summary>
public enum NbnsOpcode : byte
{
    Query = 0,
    Registration = 5,
    Release = 6,
    Wack = 7,
    Refresh = 8,
    /// <summary>Some stacks send refresh as 9 (an RFC 1002 erratum) - treated like <see cref="Refresh"/>.</summary>
    RefreshAlt = 9,
    MultihomedRegistration = 15
}

/// <summary>NBNS RCODE field (RFC 1002 §4.2.6 / §4.2.14).</summary>
public enum NbnsRcode : byte
{
    None = 0,
    FormatError = 1,
    ServerFailure = 2,
    NameError = 3,
    NotImplemented = 4,
    Refused = 5,
    /// <summary>ACT_ERR - the name is owned by another node.</summary>
    Active = 6,
    Conflict = 7
}

/// <summary>Bit masks of the 16-bit flags word: R(1) OPCODE(4) NM_FLAGS(7) RCODE(4).</summary>
public static class NbnsFlags
{
    public const ushort Response = 0x8000;
    public const ushort AuthoritativeAnswer = 0x0400;
    public const ushort Truncated = 0x0200;
    public const ushort RecursionDesired = 0x0100;
    public const ushort RecursionAvailable = 0x0080;
    public const ushort Broadcast = 0x0010;
}

/// <summary>Resource record types / class used by NBNS.</summary>
public static class NbnsType
{
    public const ushort Null = 0x000A;
    public const ushort Nb = 0x0020;
    public const ushort NbStat = 0x0021;
    public const ushort ClassIn = 0x0001;
}

/// <summary>NB_FLAGS word in NB RDATA: G(1) ONT(2) reserved(13).</summary>
public static class NbFlags
{
    public const ushort Group = 0x8000;
    public const ushort NodeTypeMask = 0x6000;

    public static string NodeTypeName(ushort nbFlags) => (nbFlags & NodeTypeMask) switch
    {
        0x0000 => "B-node",
        0x2000 => "P-node",
        0x4000 => "M-node",
        _ => "H-node"
    };
}
