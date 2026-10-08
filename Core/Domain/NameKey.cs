using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace WinsAlt.Core.Domain;

/// <summary>
/// A 16-byte NetBIOS name (15 name bytes, space padded + 1 suffix/type byte) packed into two
/// <see cref="ulong"/>s. The wire decoder produces this directly, so a dictionary lookup on the
/// query hot path costs no string and no array - just two integer compares.
/// </summary>
public readonly record struct NameKey(ulong Hi, ulong Lo)
{
    public const int Length = 16;
    public const int MaxNameChars = 15;

    /// <summary>The 16th byte: the NetBIOS suffix (0x00 workstation, 0x20 server, 0x1C DCs, ...).</summary>
    public byte Suffix => (byte)Lo;

    /// <summary>True for the "*" wildcard name used by node-status (NBSTAT) queries.</summary>
    public bool IsWildcard => Hi == 0x2A00_0000_0000_0000UL && Lo == 0;

    // The dictionary lookup on the query path calls these two for every packet. Spelled out (the
    // record-generated versions go through EqualityComparer<ulong>) so they inline to two compares
    // and one multiply-mix; the mix matters because names differ mostly in a few middle bytes.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(NameKey other) => Hi == other.Hi && Lo == other.Lo;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode()
    {
        ulong h = (Hi ^ (Lo * 0x9E3779B97F4A7C15UL)) * 0xC2B2AE3D27D4EB4FUL;
        return (int)(h ^ (h >> 32));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static NameKey FromBytes(ReadOnlySpan<byte> raw16) =>
        new(BinaryPrimitives.ReadUInt64BigEndian(raw16), BinaryPrimitives.ReadUInt64BigEndian(raw16[8..]));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CopyTo(Span<byte> destination16)
    {
        BinaryPrimitives.WriteUInt64BigEndian(destination16, Hi);
        BinaryPrimitives.WriteUInt64BigEndian(destination16[8..], Lo);
    }

    /// <summary>
    /// Builds a key from a human-typed name: upper-cased (NetBIOS clients upper-case before
    /// encoding), space padded to 15 bytes. Only printable ASCII is accepted - the wire format is
    /// OEM-codepage bytes, and guessing a code page for static names would silently mismatch.
    /// </summary>
    public static bool TryCreate(ReadOnlySpan<char> name, byte suffix, out NameKey key)
    {
        key = default;
        name = name.Trim();
        if (name.Length is 0 or > MaxNameChars) return false;

        Span<byte> raw = stackalloc byte[Length];
        raw.Fill((byte)' ');
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c is < (char)0x21 or > (char)0x7E) return false;
            raw[i] = (byte)char.ToUpperInvariant(c);
        }
        raw[15] = suffix;
        key = FromBytes(raw);
        return true;
    }

    /// <summary>The 15 name bytes, trailing padding removed; non-printable bytes shown as '.'.</summary>
    public string ToDisplayName()
    {
        Span<byte> raw = stackalloc byte[Length];
        CopyTo(raw);

        int len = MaxNameChars;
        while (len > 0 && raw[len - 1] is (byte)' ' or 0) len--;

        Span<char> chars = stackalloc char[MaxNameChars];
        for (int i = 0; i < len; i++)
            chars[i] = raw[i] is >= 0x20 and <= 0x7E ? (char)raw[i] : '.';
        return new string(chars[..len]);
    }

    /// <summary>All 16 bytes as 32 hex chars - the stable id used by the API and the database file.</summary>
    public string ToHex()
    {
        Span<byte> raw = stackalloc byte[Length];
        CopyTo(raw);
        return Convert.ToHexString(raw);
    }

    public static bool TryParseHex(ReadOnlySpan<char> hex, out NameKey key)
    {
        key = default;
        if (hex.Length != Length * 2) return false;

        Span<byte> raw = stackalloc byte[Length];
        if (Convert.FromHexString(hex, raw, out _, out int written) != System.Buffers.OperationStatus.Done || written != Length)
            return false;
        key = FromBytes(raw);
        return true;
    }

    public override string ToString() => $"{ToDisplayName()}<{Suffix:X2}>";
}
