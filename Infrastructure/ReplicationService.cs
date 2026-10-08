using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinsAlt.Core.Domain;
using WinsAlt.Web;

namespace WinsAlt.Infrastructure;

/// <summary>Another WinsAlt server whose names are copied to this one.</summary>
public sealed class ReplicationPeer
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public int Port { get; set; } = ReplicationService.DefaultPort;
    public bool Enabled { get; set; } = true;
}

/// <summary>replication.json root model.</summary>
internal sealed class ReplicationFileModel
{
    public string NodeId { get; set; } = "";
    public string Key { get; set; } = "";
    public List<ReplicationPeer> Peers { get; set; } = new();
}

// ----- wire messages (WinsAlt <-> WinsAlt only) -----
internal sealed class ReplRequest
{
    public string Op { get; set; } = "pull";
    public string NodeId { get; set; } = "";
    public string NodeName { get; set; } = "";
}

internal sealed class ReplResponse
{
    public string NodeId { get; set; } = "";
    public string NodeName { get; set; } = "";
    public List<ReplRecord> Records { get; set; } = new();
}

internal sealed class ReplRecord
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = nameof(NameKind.Unique);
    public int Flags { get; set; }
    public bool IsStatic { get; set; }
    public long RegisteredAt { get; set; }
    public List<DbMember> Members { get; set; } = new();
}

public sealed record ReplicationPeerDto(string Name, string Address, int Port, bool Enabled, string? NodeName,
    int Records, long LastSyncAt, string? LastError);

public sealed record ReplicationStatus(string NodeName, int Port, bool KeySet, string ListenerState, string? ListenerError,
    int Replicas, IReadOnlyList<ReplicationPeerDto> Peers);

/// <summary>
/// WinsAlt-to-WinsAlt replication: configuration (replication.json), peer status and the
/// snapshot format.
///
/// Model - deliberately simpler than Microsoft WINS replication:
///  - every server OWNS the names registered on it (clients' registrations, static mappings and
///    its own name) and is the only one that ever changes them;
///  - every server periodically PULLS, from each peer, the complete set of names that peer owns,
///    and keeps it as a read-only replica (<see cref="ReplicaStore"/>).
/// Whole snapshots mean there are no deltas, version vectors or tombstones to get wrong: a name
/// released or expired at its owner is simply absent from the next snapshot. A snapshot carries
/// only the peer's own names, never its replicas, so nothing can loop - and every pair of servers
/// that should see each other's names must list each other (full mesh).
///
/// Replication never removes or shortens a LOCAL record, whatever the peer holds (1.7.0-1.7.2 tried "the newer
/// claim wins" and were withdrawn): clients refresh the same name at both servers (primary + secondary WINS) and
/// some hosts register different adapters with different servers, so the peer's refresh times say nothing about
/// whether a local address is dead. Stale local addresses age out by their own expiry.
///
/// The link is a small TCP protocol of its own (not the dashboard's HTTP port, which stays on
/// localhost). Both directions are authenticated with HMAC-SHA256 over a per-connection nonce
/// using a shared replication key; data is not encrypted - it is the same name/address
/// information any client can already obtain over NBNS.
/// </summary>
public sealed class ReplicationService
{
    public const int DefaultPort = 8138;
    public const int MinKeyLength = 12;

    private sealed class Peer(ReplicationPeer config)
    {
        public readonly ReplicationPeer Config = config;
        public string? NodeName;
        public int Records;
        public long LastSyncAt;
        public string? LastError;
    }

    private readonly NameStore _store;
    private readonly ReplicaStore _replicas;
    private readonly ILogger<ReplicationService> _logger;
    private readonly string _path;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _changed = new(0);
    private List<Peer> _peers = new();
    private string _nodeId = "";
    private string _key = "";
    private volatile byte[]? _keyBytes;

    public ReplicationService(NameStore store, ReplicaStore replicas, WinsOptions options, ILogger<ReplicationService> logger)
    {
        _store = store;
        _replicas = replicas;
        _logger = logger;
        _path = Path.Combine(options.DataDirectory, "replication.json");
        Port = options.ReplicationPort;
        NodeName = Environment.MachineName.ToUpperInvariant();
        Load();
    }

    public int Port { get; }
    public string NodeName { get; }
    public string NodeId => _nodeId;

    /// <summary>SHA-256 of the shared key, or null while no key is set (replication is then off).</summary>
    public byte[]? KeyBytes => _keyBytes;

    public string ListenerState { get; set; } = "Starting";
    public string? ListenerError { get; set; }

    /// <summary>Released when peers or the key change, so the pull loop syncs at once.</summary>
    public SemaphoreSlim Changed => _changed;

    public ReplicationStatus GetStatus()
    {
        lock (_lock)
        {
            return new ReplicationStatus(NodeName, Port, _keyBytes is not null, ListenerState, ListenerError, _replicas.Count,
                _peers.Select(p => new ReplicationPeerDto(p.Config.Name, p.Config.Address, p.Config.Port, p.Config.Enabled,
                    p.NodeName, p.Records, p.LastSyncAt, p.LastError)).ToList());
        }
    }

    /// <summary>True when <paramref name="address"/> is one of the enabled replication peers.</summary>
    public bool IsPeer(System.Net.IPAddress address)
    {
        string text = (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
        lock (_lock) return _peers.Exists(p => p.Config.Enabled && p.Config.Address == text);
    }

    public IReadOnlyList<ReplicationPeer> EnabledPeers()
    {
        lock (_lock) return _peers.Where(p => p.Config.Enabled).Select(p => p.Config).ToList();
    }

    /// <summary>The shared key in clear text - only ever handed to a dashboard admin, to copy it to another server.</summary>
    public string GetKey()
    {
        lock (_lock) return _key;
    }

    /// <summary>Sets (or, with an empty value, clears) the shared key. Returns an error message, or null.</summary>
    public string? SetKey(string? key)
    {
        key = (key ?? "").Trim();
        if (key.Length is > 0 and < MinKeyLength) return $"The replication key must be at least {MinKeyLength} characters.";

        lock (_lock)
        {
            _key = key;
            _keyBytes = key.Length > 0 ? SHA256.HashData(Encoding.UTF8.GetBytes(key)) : null;
            Save_NoLock();
        }
        Signal();
        return null;
    }

    /// <summary>Adds or replaces the peer with the same address. Returns an error message, or null.</summary>
    public string? UpsertPeer(ReplicationPeer peer)
    {
        if (!Ipv4.TryParse(peer.Address, out uint address) || address is 0 or Ipv4.LimitedBroadcast)
            return $"Invalid IPv4 address: {peer.Address}";
        if (peer.Port is < 1 or > 65535) return "Port must be between 1 and 65535.";

        peer.Address = Ipv4.ToString(address);
        peer.Name = (peer.Name ?? "").Trim();

        lock (_lock)
        {
            _peers.RemoveAll(p => p.Config.Address == peer.Address);
            _peers.Add(new Peer(peer));
            _peers.Sort(static (a, b) => string.CompareOrdinal(a.Config.Address, b.Config.Address));
            Save_NoLock();
            ForgetStaleReplicas_NoLock();
        }
        Signal();
        return null;
    }

    public bool DeletePeer(string address)
    {
        lock (_lock)
        {
            if (_peers.RemoveAll(p => p.Config.Address == address.Trim()) == 0) return false;
            Save_NoLock();
            ForgetStaleReplicas_NoLock();
        }
        Signal();
        return true;
    }

    /// <summary>Everything this server owns, serialized for a peer.</summary>
    public byte[] BuildSnapshot()
    {
        long now = Clock.UnixNow();
        var response = new ReplResponse { NodeId = _nodeId, NodeName = NodeName };
        foreach (var record in _store.Records)
        {
            var row = new ReplRecord
            {
                Name = record.Key.ToHex(),
                Kind = record.Kind.ToString(),
                Flags = record.NbFlags,
                IsStatic = record.IsStatic,
                RegisteredAt = record.RegisteredAt
            };
            foreach (var m in record.Members)
                if (m.ExpiresAt > now) row.Members.Add(new DbMember { Ip = Ipv4.ToString(m.Address), ExpiresAt = m.ExpiresAt });
            if (row.Members.Count > 0) response.Records.Add(row);
        }
        return JsonSerializer.SerializeToUtf8Bytes(response, ReplJsonContext.Default.ReplResponse);
    }

    public byte[] BuildRequest() => JsonSerializer.SerializeToUtf8Bytes(
        new ReplRequest { NodeId = _nodeId, NodeName = NodeName }, ReplJsonContext.Default.ReplRequest);

    /// <summary>Stores a snapshot pulled from <paramref name="peer"/>. Returns an error message, or null.</summary>
    public string? ApplySnapshot(ReplicationPeer peer, byte[] body)
    {
        var response = JsonSerializer.Deserialize(body, ReplJsonContext.Default.ReplResponse);
        if (response is null) return "The peer sent an empty answer.";
        if (response.NodeId == _nodeId) return "That address is this server itself.";

        string origin = peer.Name.Length > 0 ? peer.Name : response.NodeName;
        var records = new List<ReplicaRecord>(response.Records.Count);
        foreach (var r in response.Records)
        {
            if (!NameKey.TryParseHex(r.Name, out var key) || !Enum.TryParse<NameKind>(r.Kind, ignoreCase: true, out var kind)) continue;

            var members = new List<NameMember>(r.Members.Count);
            foreach (var m in r.Members)
                if (Ipv4.TryParse(m.Ip, out uint ip)) members.Add(new NameMember(ip, m.ExpiresAt));
            if (members.Count > 0)
                records.Add(new ReplicaRecord(key, kind, (ushort)r.Flags, r.IsStatic, r.RegisteredAt, members.ToArray(), origin));
        }

        // Do not resurrect a peer that was removed or paused while this pull was in flight.
        lock (_lock)
        {
            var entry = _peers.FirstOrDefault(p => p.Config.Address == peer.Address && p.Config.Enabled);
            if (entry is null) return null;

            _replicas.SetPeer(peer.Address, records);
            if (entry.LastSyncAt == 0 || entry.LastError is not null)
                _logger.LogInformation("Replication with {Peer} ({Node}) is working: {Count} name(s) received",
                    peer.Address, response.NodeName, records.Count);
            entry.NodeName = response.NodeName;
            entry.Records = records.Count;
            entry.LastSyncAt = Clock.UnixNow();
            entry.LastError = null;
        }

        return null;
    }

    public void ReportFailure(ReplicationPeer peer, string error)
    {
        lock (_lock)
        {
            var entry = _peers.FirstOrDefault(p => p.Config.Address == peer.Address);
            if (entry is null) return;
            if (entry.LastError != error)
                _logger.LogWarning("Replication with {Peer} failed: {Error}", peer.Address, error);
            entry.LastError = error;
        }
    }

    private void Signal()
    {
        if (_changed.CurrentCount == 0) _changed.Release();
    }

    private void ForgetStaleReplicas_NoLock() =>
        _replicas.KeepOnly(_peers.Where(p => p.Config.Enabled).Select(p => p.Config.Address).ToHashSet());

    private void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_path))
                {
                    var model = JsonSerializer.Deserialize(File.ReadAllText(_path), ConfigJsonContext.Default.ReplicationFileModel);
                    if (model is not null)
                    {
                        _nodeId = model.NodeId ?? "";
                        _key = (model.Key ?? "").Trim();
                        _peers = model.Peers.Where(p => Ipv4.TryParse(p.Address, out _)).Select(p => new Peer(p)).ToList();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read replication.json - replication is off until it is configured again");
            }

            _keyBytes = _key.Length >= MinKeyLength ? SHA256.HashData(Encoding.UTF8.GetBytes(_key)) : null;
            if (_nodeId.Length == 0)
            {
                // A stable identity so a server can recognise (and refuse) itself as a peer.
                _nodeId = Guid.NewGuid().ToString("N");
                try { Save_NoLock(); }
                catch (Exception ex) { _logger.LogWarning("Could not write replication.json: {Msg}", ex.Message); }
            }
        }
    }

    private void Save_NoLock()
    {
        var model = new ReplicationFileModel { NodeId = _nodeId, Key = _key, Peers = _peers.Select(p => p.Config).ToList() };
        // Atomic write: temp file then rename-replace (see WinsDatabase).
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(model, ConfigJsonContext.Default.ReplicationFileModel));
        File.Move(tmp, _path, overwrite: true);
    }
}

/// <summary>
/// Framing for the replication link: <c>[int32 length][body][32-byte HMAC]</c>. The HMAC covers
/// the server's per-connection nonce, a direction byte and the body, so a captured frame cannot
/// be replayed on another connection or reflected back as the opposite direction.
/// </summary>
internal static class ReplicationWire
{
    public const int NonceLength = 16;
    public const byte Request = (byte)'Q';
    public const byte Response = (byte)'R';
    private const int MacLength = 32;

    public static async Task WriteFrameAsync(Stream stream, byte[] body, byte[] key, byte[] nonce, byte direction, CancellationToken ct)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, body.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(body, ct);
        await stream.WriteAsync(Mac(key, nonce, direction, body), ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>Reads one frame. Returns null when the HMAC does not verify (wrong key) or the frame is oversized.</summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, byte[] key, byte[] nonce, byte direction, int maxLength, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 0 || length > maxLength) return null;

        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        var mac = new byte[MacLength];
        await stream.ReadExactlyAsync(mac, ct);

        return CryptographicOperations.FixedTimeEquals(mac, Mac(key, nonce, direction, body)) ? body : null;
    }

    private static byte[] Mac(byte[] key, byte[] nonce, byte direction, byte[] body)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        hmac.AppendData(nonce);
        hmac.AppendData([direction]);
        hmac.AppendData(body);
        return hmac.GetHashAndReset();
    }
}
