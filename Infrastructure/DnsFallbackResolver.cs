using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using WinsAlt.Core.Domain;

namespace WinsAlt.Infrastructure;

public enum DnsCacheState : byte { Unknown, Positive, Negative }

/// <summary>
/// DNS fallback for names that are not in the WINS database (the "Enable DNS for NetBIOS
/// resolution" behaviour). Only the two host-like suffixes are eligible: 0x00 (workstation) and
/// 0x20 (file server).
///
/// The packet path only ever calls <see cref="TryGetCached"/> - a lock-free dictionary read. An
/// actual lookup runs off the packet path, is de-duplicated per name, and its result (hit or
/// miss) is cached so a client retransmitting the same query cannot multiply upstream traffic.
///
/// With <c>Wins:Dns:Servers</c> set, queries go straight to those servers over UDP 53 using the
/// minimal client below; otherwise the operating system resolver is used.
/// </summary>
public sealed class DnsFallbackResolver
{
    private readonly record struct CacheEntry(uint Address, long ExpiresAt);

    private const int MaxCacheEntries = 20_000;
    private const int MaxConcurrentLookups = 64;

    private readonly WinsOptions _options;
    private readonly ILogger<DnsFallbackResolver> _logger;
    private readonly ConcurrentDictionary<NameKey, CacheEntry> _cache = new();
    private readonly ConcurrentDictionary<NameKey, Task<uint>> _inFlight = new();
    private int _active;

    public DnsFallbackResolver(WinsOptions options, ILogger<DnsFallbackResolver> logger)
    {
        _options = options;
        _logger = logger;
    }

    public bool Enabled => _options.DnsEnabled;

    /// <summary>Only workstation/server names with hostname-safe characters are sent to DNS.</summary>
    public static bool IsEligible(NameKey key)
    {
        if (key.Suffix is not (0x00 or 0x20)) return false;

        Span<byte> raw = stackalloc byte[NameKey.Length];
        key.CopyTo(raw);

        int length = 0;
        for (int i = 0; i < NameKey.MaxNameChars; i++)
        {
            byte b = raw[i];
            if (b == (byte)' ' || b == 0) break;
            bool ok = b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z'
                        or >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'_';
            if (!ok) return false;
            length++;
        }
        if (length == 0) return false;

        // Whatever follows the first pad byte must be padding too (no embedded spaces).
        for (int i = length; i < NameKey.MaxNameChars; i++)
            if (raw[i] != (byte)' ' && raw[i] != 0) return false;
        return true;
    }

    /// <summary>Hot-path cache probe. Allocation-free.</summary>
    public DnsCacheState TryGetCached(NameKey key, long now, out uint address)
    {
        address = 0;
        if (!_cache.TryGetValue(Canonical(key), out var entry) || entry.ExpiresAt <= now) return DnsCacheState.Unknown;
        address = entry.Address;
        return entry.Address != 0 ? DnsCacheState.Positive : DnsCacheState.Negative;
    }

    /// <summary>
    /// Resolves through DNS (joining an identical lookup already in flight). Returns 0 when the
    /// name does not resolve or the resolver is saturated.
    /// </summary>
    public Task<uint> ResolveAsync(NameKey key, CancellationToken ct)
    {
        key = Canonical(key);
        if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAt > Clock.UnixNow()) return Task.FromResult(cached.Address);
        if (_inFlight.TryGetValue(key, out var running)) return running;
        if (Volatile.Read(ref _active) >= MaxConcurrentLookups) return Task.FromResult(0u);

        return _inFlight.GetOrAdd(key, static (k, state) => state.self.LookupAsync(k, state.ct), (self: this, ct));
    }

    /// <summary>Forgets every cached answer (hits and misses). Returns how many were dropped.</summary>
    public int ClearCache()
    {
        int count = _cache.Count;
        _cache.Clear();
        return count;
    }

    /// <summary>Drops expired cache entries (called by the maintenance sweep).</summary>
    public void Prune(long now)
    {
        foreach (var (key, entry) in _cache)
            if (entry.ExpiresAt <= now) _cache.TryRemove(key, out _);
    }

    // 0x00 and 0x20 of the same machine name are the same DNS host - share one cache slot.
    private static NameKey Canonical(NameKey key) => new(key.Hi, key.Lo & ~0xFFUL);

    private async Task<uint> LookupAsync(NameKey key, CancellationToken ct)
    {
        uint address = 0;
        Interlocked.Increment(ref _active);
        try
        {
            // Yield first so the caller's GetOrAdd publishes this task before it can complete.
            await Task.Yield();

            string host = key.ToDisplayName().ToLowerInvariant();
            if (_options.DnsSuffix.Length > 0) host = $"{host}.{_options.DnsSuffix}";

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.DnsTimeoutMs);

            address = _options.DnsServers.Length > 0
                ? await QueryServersAsync(host, timeout.Token)
                : await QuerySystemAsync(host, timeout.Token);
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { /* NXDOMAIN / no such host / server unreachable - a miss */ }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DNS fallback lookup failed for {Name}", key);
        }
        finally
        {
            int ttl = address != 0 ? _options.DnsCacheSeconds : _options.DnsNegativeCacheSeconds;
            if (ttl > 0 && _cache.Count < MaxCacheEntries)
                _cache[key] = new CacheEntry(address, Clock.UnixNow() + ttl);

            _inFlight.TryRemove(key, out _);
            Interlocked.Decrement(ref _active);
        }
        return address;
    }

    private static async Task<uint> QuerySystemAsync(string host, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct);
        foreach (var candidate in addresses)
        {
            uint value = Ipv4.FromIPAddress(candidate);
            if (Ipv4.IsUsableHost(value)) return value;
        }
        return 0;
    }

    private async Task<uint> QueryServersAsync(string host, CancellationToken ct)
    {
        var query = new byte[512];
        int queryLength = WriteDnsQuery(query, host, out ushort id);
        if (queryLength == 0) return 0;

        var response = new byte[512];
        foreach (var server in _options.DnsServers)
        {
            try
            {
                using var socket = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                await socket.ConnectAsync(new IPEndPoint(server, 53), ct);
                await socket.SendAsync(query.AsMemory(0, queryLength), SocketFlags.None, ct);
                int received = await socket.ReceiveAsync(response, SocketFlags.None, ct);

                if (TryReadDnsAnswer(response.AsSpan(0, received), query.AsSpan(0, queryLength), id, out uint address, out bool authoritativeMiss))
                    return address;
                if (authoritativeMiss) return 0; // NXDOMAIN / NODATA - asking the next server won't differ
            }
            catch (SocketException ex)
            {
                _logger.LogDebug("DNS server {Server} failed: {Msg}", server, ex.Message);
            }
        }
        return 0;
    }

    /// <summary>Builds a standard recursive A/IN query. Returns 0 if the host name is not encodable.</summary>
    private static int WriteDnsQuery(Span<byte> buffer, string host, out ushort id)
    {
        id = (ushort)RandomNumberGenerator.GetInt32(1, 65536);
        BinaryPrimitives.WriteUInt16BigEndian(buffer, id);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[2..], 0x0100); // RD
        BinaryPrimitives.WriteUInt16BigEndian(buffer[4..], 1);      // QDCOUNT
        buffer[6..12].Clear();

        int pos = 12;
        foreach (var range in host.AsSpan().Split('.'))
        {
            var label = host.AsSpan(range);
            if (label.Length is 0 or > 63 || pos + 1 + label.Length > 255 + 12) return 0;
            buffer[pos++] = (byte)label.Length;
            pos += Encoding.ASCII.GetBytes(label, buffer[pos..]);
        }
        buffer[pos++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(buffer[pos..], 1);     // QTYPE A
        BinaryPrimitives.WriteUInt16BigEndian(buffer[(pos + 2)..], 1); // QCLASS IN
        return pos + 4;
    }

    /// <summary>Pulls the first A record out of a DNS response (CNAME chains resolve to the A that follows).</summary>
    private static bool TryReadDnsAnswer(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> query, ushort id, out uint address, out bool authoritativeMiss)
    {
        address = 0;
        authoritativeMiss = false;
        if (packet.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(packet) != id) return false;

        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        if ((flags & 0x8000) == 0) return false;
        int rcode = flags & 0x000F;
        if (rcode == 3) { authoritativeMiss = true; return false; }
        if (rcode != 0) return false;

        int questions = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
        int answers = BinaryPrimitives.ReadUInt16BigEndian(packet[6..]);
        int pos = 12;

        // The response must repeat exactly the question that was asked (name, type, class). An
        // answer to some other question - forged, or a confused server - is never cached.
        var question = query[12..];
        if (questions != 1 || packet.Length < 12 + question.Length || !packet.Slice(12, question.Length).SequenceEqual(question)) return false;

        for (int i = 0; i < questions; i++)
        {
            if (!TrySkipDnsName(packet, ref pos) || pos + 4 > packet.Length) return false;
            pos += 4;
        }

        for (int i = 0; i < answers; i++)
        {
            if (!TrySkipDnsName(packet, ref pos) || pos + 10 > packet.Length) return false;
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(packet[pos..]);
            ushort length = BinaryPrimitives.ReadUInt16BigEndian(packet[(pos + 8)..]);
            pos += 10;
            if (pos + length > packet.Length) return false;

            if (type == 1 && length == 4)
            {
                address = BinaryPrimitives.ReadUInt32BigEndian(packet[pos..]);
                if (Ipv4.IsUsableHost(address)) return true;
                address = 0;   // 0.0.0.0, loopback, multicast: not something to hand to a client
                authoritativeMiss = true;
                return false;
            }
            pos += length;
        }

        authoritativeMiss = true; // NOERROR with no A record
        return false;
    }

    private static bool TrySkipDnsName(ReadOnlySpan<byte> packet, ref int pos)
    {
        while (pos < packet.Length)
        {
            byte len = packet[pos];
            if (len == 0) { pos++; return true; }
            if ((len & 0xC0) == 0xC0) { pos += 2; return pos <= packet.Length; }
            pos += 1 + len;
        }
        return false;
    }
}
