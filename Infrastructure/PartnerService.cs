using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using WinsAlt.Core.Domain;
using WinsAlt.Core.Protocol;
using WinsAlt.Web;

namespace WinsAlt.Infrastructure;

/// <summary>Another WINS / NBNS server that names unknown here are looked up on.</summary>
public sealed class PartnerServer
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    /// <summary>Always 137 for a real WINS server; only the test suite uses another port.</summary>
    public int Port { get; set; } = 137;
    public bool Enabled { get; set; } = true;
}

/// <summary>partners.json root model.</summary>
internal sealed class PartnerFileModel
{
    public List<PartnerServer> Partners { get; set; } = new();
}

/// <summary>A partner with its live counters, as shown on the dashboard.</summary>
public sealed record PartnerDto(string Name, string Address, int Port, bool Enabled,
    long Queries, long Answers, long NotFound, long NoReply, long LastReplyAt);

/// <summary>What a partner answered for a name.</summary>
public sealed record PartnerAnswer(uint[] Addresses, ushort NbFlags, string Source);

/// <summary>A partner answer currently held in the forwarding cache (for the dashboard's name list).</summary>
public readonly record struct CachedPartnerName(NameKey Key, PartnerAnswer Answer, long ExpiresAt);

/// <summary>
/// The list of partner WINS servers (partners.json, managed from the dashboard) and name lookups
/// against them.
///
/// This is query forwarding, not WINS replication: when a client asks for a name this server
/// does not hold, the same name query is sent to every enabled partner as a normal unicast NBNS
/// query (UDP 137) and the first positive answer is relayed. It works against any NBNS server  - 
/// Microsoft WINS, Samba, another WinsAlt - because it only uses the client-facing protocol.
/// Names registered here are NOT copied to the partners.
///
/// Like DNS fallback, the packet path only probes the cache; the lookup itself runs on a task,
/// is de-duplicated per name, and caches both hits and misses.
/// </summary>
public sealed class PartnerService
{
    private sealed class Partner(PartnerServer config, uint address)
    {
        public readonly PartnerServer Config = config;
        public readonly uint Address = address;
        public readonly IPEndPoint EndPoint = new(Ipv4.ToIPAddress(address), config.Port);
        public long Queries, Answers, NotFound, NoReply, LastReplyAt;
        /// <summary>No-replies in a row; reset by any reply.</summary>
        public int SilentStreak;
        /// <summary>While silent: the next time (unix seconds) a lookup will wait for this partner again.</summary>
        public long NextProbeAt;
    }

    private readonly record struct CacheEntry(PartnerAnswer? Answer, long ExpiresAt);

    private const int MaxCacheEntries = 20_000;
    private const int MaxConcurrentLookups = 64;
    private const int TimeoutMs = 1500;
    private const int CacheSeconds = 300;
    private const int NegativeCacheSeconds = 60;
    // A partner that stays silent (down, or UDP 137 blocked on the way) must not add the full
    // timeout to every lookup: after this many misses in a row it is still sent each query, but
    // lookups only wait for its answer once per probe interval until it replies again.
    private const int SilentAfterMisses = 3;
    private const int ProbeIntervalSeconds = 30;

    private readonly ILogger<PartnerService> _logger;
    private readonly string _path;
    private readonly Lock _lock = new();
    private readonly ConcurrentDictionary<NameKey, CacheEntry> _cache = new();
    private readonly ConcurrentDictionary<NameKey, Task<PartnerAnswer?>> _inFlight = new();
    private volatile Partner[] _partners = [];
    private volatile Partner[] _active = [];
    private int _running;


    public PartnerService(WinsOptions options, ILogger<PartnerService> logger)
    {
        _logger = logger;
        _path = Path.Combine(options.DataDirectory, "partners.json");
        Load();
    }

    /// <summary>True when at least one partner is enabled.</summary>
    public bool Enabled => _active.Length > 0;

    /// <summary>Queries arriving FROM a partner are never forwarded back out - that would loop.</summary>
    public bool IsPartner(uint address)
    {
        foreach (var p in _active)
            if (p.Address == address) return true;
        return false;
    }

    public IReadOnlyList<PartnerDto> GetAll() => _partners.Select(p => new PartnerDto(
        p.Config.Name, p.Config.Address, p.Config.Port, p.Config.Enabled,
        Interlocked.Read(ref p.Queries), Interlocked.Read(ref p.Answers), Interlocked.Read(ref p.NotFound),
        Interlocked.Read(ref p.NoReply), Interlocked.Read(ref p.LastReplyAt))).ToList();

    /// <summary>Adds or replaces the partner with the same address. Returns an error message, or null.</summary>
    public string? Upsert(PartnerServer server)
    {
        if (!Ipv4.TryParse(server.Address, out uint address) || address is 0 or Ipv4.LimitedBroadcast)
            return $"Invalid IPv4 address: {server.Address}";
        if (server.Port is < 1 or > 65535) return "Port must be between 1 and 65535.";

        server.Address = Ipv4.ToString(address);
        server.Name = (server.Name ?? "").Trim();

        lock (_lock)
        {
            var list = _partners.Where(p => p.Address != address).ToList();
            // Keep the counters when an existing partner is only renamed / toggled.
            var existing = _partners.FirstOrDefault(p => p.Address == address && p.Config.Port == server.Port);
            var partner = new Partner(server, address);
            if (existing is not null)
            {
                partner.Queries = existing.Queries; partner.Answers = existing.Answers; partner.NotFound = existing.NotFound;
                partner.NoReply = existing.NoReply; partner.LastReplyAt = existing.LastReplyAt;
            }
            list.Add(partner);
            Publish_NoLock(list);
            Save_NoLock();
        }
        return null;
    }

    public bool Delete(string addressText)
    {
        if (!Ipv4.TryParse(addressText, out uint address)) return false;
        lock (_lock)
        {
            var list = _partners.Where(p => p.Address != address).ToList();
            if (list.Count == _partners.Length) return false;
            Publish_NoLock(list);
            Save_NoLock();
            return true;
        }
    }

    /// <summary>Hot-path cache probe. Copies a cached answer into <paramref name="addresses"/>. Allocation-free.</summary>
    public DnsCacheState TryGetCached(NameKey key, long now, Span<uint> addresses, out int count, out ushort nbFlags)
    {
        count = 0;
        nbFlags = 0;
        if (!_cache.TryGetValue(key, out var entry) || entry.ExpiresAt <= now) return DnsCacheState.Unknown;
        if (entry.Answer is not { } answer) return DnsCacheState.Negative;

        count = Math.Min(answer.Addresses.Length, addresses.Length);
        answer.Addresses.AsSpan(0, count).CopyTo(addresses);
        nbFlags = answer.NbFlags;
        return DnsCacheState.Positive;
    }

    /// <summary>Asks the partners (joining an identical lookup already in flight). Null = no partner knows the name.</summary>
    public Task<PartnerAnswer?> ResolveAsync(NameKey key, CancellationToken ct)
    {
        if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAt > Clock.UnixNow()) return Task.FromResult(cached.Answer);
        if (_inFlight.TryGetValue(key, out var running)) return running;
        if (_active.Length == 0 || Volatile.Read(ref _running) >= MaxConcurrentLookups) return Task.FromResult<PartnerAnswer?>(null);

        return _inFlight.GetOrAdd(key, static (k, state) => state.self.LookupAsync(k, state.ct), (self: this, ct));
    }

    /// <summary>
    /// Names recently answered by a partner. They are not part of the database - only a short-lived
    /// cache of forwarded answers - but showing them tells the operator where a client's answer came from.
    /// </summary>
    public IEnumerable<CachedPartnerName> CachedNames(long now)
    {
        foreach (var (key, entry) in _cache)
            if (entry.ExpiresAt > now && entry.Answer is { } answer) yield return new CachedPartnerName(key, answer, entry.ExpiresAt);
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

    private async Task<PartnerAnswer?> LookupAsync(NameKey key, CancellationToken ct)
    {
        PartnerAnswer? answer = null;
        bool conclusive = false;
        Interlocked.Increment(ref _running);
        try
        {
            // Yield first so the caller's GetOrAdd publishes this task before it can complete.
            await Task.Yield();

            var partners = _active;
            if (partners.Length == 0) return null;

            // A socket of our own (not the port-137 listener): replies come straight back here
            // and cannot be confused with client traffic.
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            DisableConnReset(socket);
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));

            ushort id = (ushort)System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, 65536); // not guessable
            var query = new byte[NbnsHeader.Size + NbnsName.EncodedLength + 4];
            int queryLength = NbnsPackets.WriteNameQuery(query, id, key, recursionDesired: true);

            long startedAt = Clock.UnixNow();
            var pending = new HashSet<Partner>();
            foreach (var partner in partners)
            {
                if (Volatile.Read(ref partner.SilentStreak) < SilentAfterMisses)
                    pending.Add(partner);
                else if (startedAt >= Interlocked.Read(ref partner.NextProbeAt))
                {
                    Interlocked.Exchange(ref partner.NextProbeAt, startedAt + ProbeIntervalSeconds);
                    pending.Add(partner);
                }

                Interlocked.Increment(ref partner.Queries);
                try { await socket.SendToAsync(query.AsMemory(0, queryLength), SocketFlags.None, partner.EndPoint, ct); }
                catch (SocketException) { /* unreachable - counted as no reply below */ }
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeoutMs);
            var buffer = new byte[NbnsPacketWriter.MaxPacket];
            var from = new SocketAddress(AddressFamily.InterNetwork);
            var addresses = new uint[NbnsPackets.MaxAddresses];

            try
            {
                while (pending.Count > 0 && answer is null)
                {
                    int received = await socket.ReceiveFromAsync(buffer.AsMemory(), SocketFlags.None, from, timeout.Token);
                    uint sender = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(from.Buffer.Span[4..]);
                    var partner = pending.FirstOrDefault(p => p.Address == sender);
                    if (partner is null) continue;

                    var packet = buffer.AsSpan(0, received);
                    if (!NbnsMessage.TryParse(packet, out var reply) || !reply.Header.IsResponse
                        || reply.Header.TransactionId != id || reply.Name != key) continue;

                    pending.Remove(partner);
                    Interlocked.Exchange(ref partner.LastReplyAt, Clock.UnixNow());
                    Volatile.Write(ref partner.SilentStreak, 0);

                    int count = reply.Header.Rcode == NbnsRcode.None ? reply.CopyAddresses(packet, addresses) : 0;

                    // Never cache or relay an answer that is not a real host address. (A group answer of
                    // 255.255.255.255 is the one legitimate exception - that is how WINS answers a group.)
                    bool group = (reply.NbFlags & NbFlags.Group) != 0;
                    int usable = 0;
                    for (int i = 0; i < count; i++)
                        if (Ipv4.IsUsableHost(addresses[i]) || (group && addresses[i] == Ipv4.LimitedBroadcast)) addresses[usable++] = addresses[i];
                    count = usable;
                    if (count > 0)
                    {
                        Interlocked.Increment(ref partner.Answers);
                        answer = new PartnerAnswer(addresses.AsSpan(0, count).ToArray(), reply.NbFlags,
                            partner.Config.Name.Length > 0 ? partner.Config.Name : partner.Config.Address);
                    }
                    else Interlocked.Increment(ref partner.NotFound);
                }
                conclusive = true; // an answer, or every partner said "not found"
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                foreach (var silent in pending)
                {
                    Interlocked.Increment(ref silent.NoReply);
                    if (Interlocked.Increment(ref silent.SilentStreak) == SilentAfterMisses)
                        Interlocked.Exchange(ref silent.NextProbeAt, Clock.UnixNow() + ProbeIntervalSeconds);
                }
                conclusive = true; // timed out - cache the miss briefly so retransmits do not re-ask
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Partner lookup failed for {Name}", key);
        }
        finally
        {
            if (conclusive && _cache.Count < MaxCacheEntries)
                _cache[key] = new CacheEntry(answer, Clock.UnixNow() + (answer is not null ? CacheSeconds : NegativeCacheSeconds));

            _inFlight.TryRemove(key, out _);
            Interlocked.Decrement(ref _running);
        }
        return answer;
    }

    private void Load()
    {
        lock (_lock)
        {
            var list = new List<Partner>();
            try
            {
                if (File.Exists(_path))
                {
                    var model = JsonSerializer.Deserialize(File.ReadAllText(_path), ConfigJsonContext.Default.PartnerFileModel);
                    foreach (var server in model?.Partners ?? new())
                    {
                        if (Ipv4.TryParse(server.Address, out uint address) && server.Port is >= 1 and <= 65535)
                            list.Add(new Partner(server, address));
                        else
                            _logger.LogWarning("Skipped partner '{Name}': invalid address {Address}", server.Name, server.Address);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read partners.json - using no partner servers");
            }
            Publish_NoLock(list);
        }
    }

    private void Publish_NoLock(List<Partner> list)
    {
        list.Sort(static (a, b) => a.Address.CompareTo(b.Address));
        _partners = list.ToArray();
        _active = list.Where(p => p.Config.Enabled).ToArray();
        _cache.Clear(); // the answer to "does any partner know this name" may have changed
    }

    private void Save_NoLock()
    {
        var model = new PartnerFileModel { Partners = _partners.Select(p => p.Config).ToList() };
        // Atomic write: temp file then rename-replace (see WinsDatabase).
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(model, ConfigJsonContext.Default.PartnerFileModel));
        File.Move(tmp, _path, overwrite: true);
    }

    private static void DisableConnReset(Socket socket)
    {
        // SIO_UDP_CONNRESET: a partner that is down answers with ICMP "port unreachable", which
        // would otherwise fail the receive that is waiting for the other partners.
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            const int SIO_UDP_CONNRESET = -1744830452; // unchecked((int)0x9800000C)
            socket.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
        }
        catch (SocketException) { /* best-effort */ }
    }
}
