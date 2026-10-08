using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace WinsAlt.Core.Domain;

/// <summary>
/// IPv4 addresses are carried as a host-order numeric <see cref="uint"/> (192.168.1.10 =
/// 0xC0A8010A) everywhere below the API - NetBIOS is IPv4-only and a uint keeps the packet path
/// free of <see cref="IPAddress"/> allocations.
/// </summary>
public static class Ipv4
{
    /// <summary>255.255.255.255 - what WINS returns for a normal group name.</summary>
    public const uint LimitedBroadcast = 0xFFFFFFFF;

    /// <summary>
    /// False for addresses that can never be a host a client should connect to: 0.x.x.x,
    /// multicast and reserved space (224.0.0.0 and above, which includes 255.255.255.255), and  - 
    /// unless <paramref name="allowLoopback"/> - 127.x.x.x. Used to keep such values out of the
    /// database and out of the forwarding caches, whoever supplied them.
    /// </summary>
    public static bool IsUsableHost(uint address, bool allowLoopback = false)
    {
        uint first = address >> 24;
        return first != 0 && first < 224 && (allowLoopback || first != 127);
    }

    public static bool TryParse(ReadOnlySpan<char> text, out uint value)
    {
        value = 0;
        text = text.Trim();
        // IPAddress.TryParse accepts shorthand like "1" or "10.1"; require the dotted quad.
        if (text.Count('.') != 3) return false;
        if (!IPAddress.TryParse(text, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;

        value = FromIPAddress(ip);
        return true;
    }

    public static uint FromIPAddress(IPAddress ip)
    {
        Span<byte> bytes = stackalloc byte[4];
        ip.TryWriteBytes(bytes, out _);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    public static IPAddress ToIPAddress(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }

    public static string ToString(uint value) =>
        string.Create(null, stackalloc char[15], $"{(byte)(value >> 24)}.{(byte)(value >> 16)}.{(byte)(value >> 8)}.{(byte)value}");
}

public static class Clock
{
    /// <summary>Wall-clock unix seconds (UTC). Expiry is persisted, so it cannot be a monotonic tick count.</summary>
    public static long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public static long UnixNowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
