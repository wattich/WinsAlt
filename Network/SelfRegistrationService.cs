using System.Buffers.Binary;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using WinsAlt.Core.Domain;
using WinsAlt.Core.Protocol;
using WinsAlt.Infrastructure;

namespace WinsAlt.Network;

/// <summary>
/// Stands in for the NetBIOS presence this machine loses when Windows' NetBIOS over TCP/IP is
/// switched off so that the name server can have UDP 137:
///
///  - its own computer name is kept in the database (suffix 00 workstation + 20 file server by
///    default, more through <c>Wins:SelfSuffixes</c>), pointing at its current address(es);
///  - it is registered as a member of its workgroup / domain group name (&lt;00&gt; group), as
///    Windows itself would do;
///  - a node status answer (what <c>nbtstat -A &lt;ip&gt;</c> and network scanners ask for) is
///    prepared, listing those names and the adapter's MAC address.
///
/// Everything is re-derived whenever Windows reports an address change (and on a slow timer as a
/// backstop), so a new IP is picked up without touching any configuration.
///
/// What cannot be replaced from user mode: the datagram service (UDP 138 - browsing, mailslots),
/// the session service (TCP 139), and this machine's own use of WINS as a client.
/// </summary>
public sealed class SelfRegistrationService : BackgroundService
{
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(60);
    private const ushort ActiveName = 0x0400;       // NAME_FLAGS: ACT
    private const int StatisticsLength = 46;        // 6-byte unit id (MAC) + 40 bytes of counters, all zero here

    private readonly StaticMappingService _mappings;
    private readonly NameStore _store;
    private readonly WinsOptions _options;
    private readonly ILogger<SelfRegistrationService> _logger;
    private readonly SemaphoreSlim _changed = new(0);
    private readonly byte[] _suffixes;
    private uint[] _published = [];
    private volatile IReadOnlyList<string> _addresses = [];
    private volatile byte[]? _nodeStatus;
    private NameKey _groupKey;
    private readonly bool _hasGroup;

    public SelfRegistrationService(StaticMappingService mappings, NameStore store, WinsOptions options, ILogger<SelfRegistrationService> logger)
    {
        _mappings = mappings;
        _store = store;
        _options = options;
        _logger = logger;
        _suffixes = options.SelfSuffixes;
        Name = (options.SelfName.Length > 0 ? options.SelfName : Environment.MachineName).ToUpperInvariant();

        Group = options.SelfGroup switch
        {
            "-" => "",                                  // explicitly none
            { Length: > 0 } configured => configured.ToUpperInvariant(),
            _ => DetectWorkgroupOrDomain()
        };
        _hasGroup = Group.Length > 0 && Group != Name && NameKey.TryCreate(Group, 0x00, out _groupKey);
        if (!_hasGroup) Group = "";
    }

    public bool Enabled => _options.RegisterSelf;
    public string Name { get; }
    /// <summary>The workgroup / domain name this machine is announced as a member of ("" = none).</summary>
    public string Group { get; }
    /// <summary>The addresses currently published for <see cref="Name"/>.</summary>
    public IReadOnlyList<string> Addresses => _addresses;

    /// <summary>
    /// The RDATA of a node status response for this machine (name table + statistics), rebuilt
    /// only when the addresses change - the packet path just copies it. Null while nothing is published.
    /// </summary>
    public byte[]? NodeStatus => _nodeStatus;

    /// <summary>True when <paramref name="key"/> is one of the names this machine answers a node status query for.</summary>
    public bool Owns(NameKey key)
    {
        if (_nodeStatus is null) return false;
        foreach (byte suffix in _suffixes)
            if (NameKey.TryCreate(Name, suffix, out var own) && own == key) return true;
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled) return;
        if (!NameKey.TryCreate(Name, 0, out _))
        {
            _logger.LogWarning("Not publishing this server's own name: '{Name}' is not a valid NetBIOS name (set Wins:SelfName)", Name);
            return;
        }
        // A fresh Linux install is often still "localhost.localdomain": publishing LOCALHOST (and replicating it to every
        // peer) would only mislead. Ask for a real name instead.
        if (Name is "LOCALHOST" or "LOCALHOST.LOCALDOMAIN")
        {
            _logger.LogWarning("Not publishing this server's own name: the host name is still '{Name}' - set one (hostnamectl set-hostname) " +
                "or Wins:SelfName, then restart the service", Environment.MachineName);
            return;
        }

        void OnAddressChanged(object? sender, EventArgs e)
        {
            if (_changed.CurrentCount == 0) _changed.Release();
        }

        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Publish();
                await _changed.WaitAsync(RecheckInterval, stoppingToken);
                // An address change arrives as a burst of events while the adapter settles.
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        }
    }

    private void Publish()
    {
        try
        {
            uint[] addresses = CurrentAddresses();
            bool changed = !addresses.AsSpan().SequenceEqual(_published);

            if (changed)
            {
                var entries = new List<StaticEntry>();
                if (addresses.Length > 0)
                {
                    var kind = addresses.Length > 1 ? NameKind.Multihomed : NameKind.Unique;
                    foreach (byte suffix in _suffixes)
                    {
                        NameKey.TryCreate(Name, suffix, out var key);
                        entries.Add(new StaticEntry(key, kind, addresses, "This server (automatic)"));
                    }
                }
                _mappings.SetAutomaticEntries(entries);
            }

            // Group membership is an ordinary (dynamic) registration, so this machine sits in the
            // member list beside the clients; it is renewed on every pass and so never expires.
            if (_hasGroup)
            {
                long now = Clock.UnixNow();
                if (changed)
                    foreach (uint old in _published)
                        if (Array.IndexOf(addresses, old) < 0) _store.Release(_groupKey, old, now);
                foreach (uint address in addresses)
                    _store.Register(_groupKey, address, NbFlags.Group, NameKind.Group, now, _options.MaxTtlSeconds, out _);
            }

            if (!changed) return;

            _nodeStatus = addresses.Length > 0 ? BuildNodeStatus(addresses[0]) : null;
            _published = addresses;
            _addresses = addresses.Select(Ipv4.ToString).ToList();

            if (addresses.Length > 0)
                _logger.LogInformation("This server is published as {Name} -> {Addresses}{Group}", Name, string.Join(", ", _addresses),
                    _hasGroup ? $" (member of {Group})" : "");
            else
                _logger.LogWarning("This server has no usable IPv4 address - its own name {Name} is not published", Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not refresh this server's own name record: {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// RFC 1002 §4.2.18 node status RDATA: NUM_NAMES, then 18 bytes per name (16 raw name bytes +
    /// NAME_FLAGS), then the statistics block that starts with the adapter's MAC address.
    /// </summary>
    private byte[] BuildNodeStatus(uint primaryAddress)
    {
        int names = _suffixes.Length + (_hasGroup ? 1 : 0);
        var rdata = new byte[1 + names * 18 + StatisticsLength];
        rdata[0] = (byte)names;

        int pos = 1;
        foreach (byte suffix in _suffixes)
        {
            NameKey.TryCreate(Name, suffix, out var key);
            key.CopyTo(rdata.AsSpan(pos));
            BinaryPrimitives.WriteUInt16BigEndian(rdata.AsSpan(pos + 16), ActiveName);
            pos += 18;
        }
        if (_hasGroup)
        {
            _groupKey.CopyTo(rdata.AsSpan(pos));
            BinaryPrimitives.WriteUInt16BigEndian(rdata.AsSpan(pos + 16), (ushort)(ActiveName | NbFlags.Group));
            pos += 18;
        }

        MacOf(primaryAddress).CopyTo(rdata.AsSpan(pos)); // the rest of the statistics block stays zero
        return rdata;
    }

    private static byte[] MacOf(uint address)
    {
        try
        {
            var wanted = Ipv4.ToIPAddress(address);
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!nic.GetIPProperties().UnicastAddresses.Any(u => u.Address.Equals(wanted))) continue;
                byte[] mac = nic.GetPhysicalAddress().GetAddressBytes();
                if (mac.Length == 6) return mac;
            }
        }
        catch (NetworkInformationException) { }
        return new byte[6];
    }

    /// <summary>
    /// The address(es) clients should use for this machine. Bound to one address: that address.
    /// Listening on all addresses: the adapters that have a default gateway (the real LAN side),
    /// which leaves out host-only / virtual-switch adapters clients could never reach; if no
    /// adapter has a gateway, every non-loopback address.
    /// </summary>
    private uint[] CurrentAddresses()
    {
        if (!_options.ListenAddress.Equals(System.Net.IPAddress.Any))
            return [Ipv4.FromIPAddress(_options.ListenAddress)];

        var routed = new List<uint>();
        var others = new List<uint>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            var properties = nic.GetIPProperties();
            bool hasGateway = properties.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any));

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                uint ip = Ipv4.FromIPAddress(unicast.Address);
                if (ip >> 16 == 0xA9FE) continue; // 169.254.x.x - no DHCP lease, not reachable
                (hasGateway ? routed : others).Add(ip);
            }
        }

        var chosen = routed.Count > 0 ? routed : others;
        chosen.Sort();
        return chosen.Distinct().Take(NbnsPackets.MaxAddresses).ToArray();
    }

    // ---- workgroup / domain name ----
    // NetGetJoinInformation is the documented way to read it; System.Management (WMI) is not
    // usable from a Native AOT binary. The signature below is fully blittable - pointers and an
    // int - so it needs no runtime marshalling and is AOT-safe.

    [DllImport("netapi32.dll")]
    private static extern int NetGetJoinInformation(IntPtr server, out IntPtr nameBuffer, out int joinStatus);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    private string DetectWorkgroupOrDomain()
    {
        if (!OperatingSystem.IsWindows()) return SambaWorkgroup();
        try
        {
            if (NetGetJoinInformation(IntPtr.Zero, out IntPtr buffer, out _) != 0 || buffer == IntPtr.Zero) return "";
            try { return (Marshal.PtrToStringUni(buffer) ?? "").Trim().ToUpperInvariant(); }
            finally { NetApiBufferFree(buffer); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogDebug("Workgroup/domain name not available: {Msg}", ex.Message);
            return "";
        }
    }

    /// <summary>
    /// Linux: the "workgroup =" line of Samba's configuration, if the Samba server is installed; otherwise none.
    /// The file alone proves nothing: RHEL / CentOS ship it (workgroup = SAMBA) with samba-common, Samba or not.
    /// </summary>
    private string SambaWorkgroup()
    {
        try
        {
            const string path = "/etc/samba/smb.conf";
            if (!File.Exists(path) || !(File.Exists("/usr/sbin/smbd") || File.Exists("/usr/bin/smbd"))) return "";
            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.StartsWith('[') && !line.Equals("[global]", StringComparison.OrdinalIgnoreCase)) break; // past [global]
                int eq = line.IndexOf('=');
                if (eq > 0 && line[..eq].Trim().Equals("workgroup", StringComparison.OrdinalIgnoreCase))
                    return line[(eq + 1)..].Trim().ToUpperInvariant();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("Samba workgroup not readable: {Msg}", ex.Message);
        }
        return "";
    }
}
