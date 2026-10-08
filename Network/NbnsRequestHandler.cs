using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using WinsAlt.Core.Domain;
using WinsAlt.Core.Protocol;
using WinsAlt.Infrastructure;

namespace WinsAlt.Network;

/// <summary>
/// Per-worker scratch state: one response buffer, one address scratch array and one reusable
/// <see cref="SocketAddress"/>. Everything a worker needs to answer a packet is allocated once
/// here, so answering allocates nothing.
/// </summary>
public sealed class WorkerContext
{
    public readonly byte[] Response = new byte[NbnsPacketWriter.MaxPacket];
    public readonly uint[] Addresses = new uint[NbnsPackets.MaxAddresses];
    private readonly SocketAddress _remote = new(AddressFamily.InterNetwork);

    public WorkerContext(Socket socket, CancellationToken stopping)
    {
        Socket = socket;
        Stopping = stopping;
    }

    public Socket Socket { get; }
    public CancellationToken Stopping { get; }

    /// <summary>
    /// Sends the first <paramref name="length"/> bytes of <see cref="Response"/>. Synchronous on
    /// purpose: a UDP send only copies into the kernel buffer, and the span + SocketAddress
    /// overload avoids both the async state machine and an IPEndPoint per reply.
    /// </summary>
    public bool Send(int length, uint address, ushort port)
    {
        // sockaddr_in: family(2) port(2, network order) addr(4, network order)
        Span<byte> sockaddr = _remote.Buffer.Span;
        BinaryPrimitives.WriteUInt16BigEndian(sockaddr[2..], port);
        BinaryPrimitives.WriteUInt32BigEndian(sockaddr[4..], address);

        try
        {
            Socket.SendTo(Response.AsSpan(0, length), SocketFlags.None, _remote);
            return true;
        }
        catch (SocketException) { return false; }
        catch (ObjectDisposedException) { return false; }
    }
}

/// <summary>
/// NBNS server semantics: decides what each request means against the <see cref="NameStore"/>
/// and writes the reply. <see cref="Process"/> is synchronous and allocation-free for the
/// common cases (query hit/miss, refresh). Only the two rare cases that must wait on the
/// network - DNS fallback and a name challenge - hop onto a task.
/// </summary>
public sealed class NbnsRequestHandler
{
    private readonly NameStore _store;
    private readonly DnsFallbackResolver _dns;
    private readonly PartnerService _partners;
    private readonly ReplicaStore _replicas;
    private readonly SelfRegistrationService _self;
    private readonly NameChallengeService _challenge;
    private readonly QueryLog _queryLog;
    private readonly WinsCounters _counters;
    private readonly WinsOptions _options;
    private readonly ILogger<NbnsRequestHandler> _logger;
    // Per-packet events must not turn a flood into a flood of log (and Windows Event Log) writes.
    private readonly LogGate _infoGate = new(perSecond: 20);
    private readonly LogGate _warnGate = new(perSecond: 5);

    public NbnsRequestHandler(NameStore store, DnsFallbackResolver dns, PartnerService partners, ReplicaStore replicas, SelfRegistrationService self, NameChallengeService challenge,
        QueryLog queryLog, WinsCounters counters, WinsOptions options, ILogger<NbnsRequestHandler> logger)
    {
        _store = store;
        _dns = dns;
        _partners = partners;
        _replicas = replicas;
        _self = self;
        _challenge = challenge;
        _queryLog = queryLog;
        _counters = counters;
        _options = options;
        _logger = logger;
    }

    public void Process(ReadOnlySpan<byte> packet, uint clientAddress, ushort clientPort, WorkerContext ctx)
    {
        if (!NbnsMessage.TryParse(packet, out var message))
        {
            WinsCounters.Inc(ref _counters.Malformed);
            return;
        }

        if (message.Header.IsResponse)
        {
            // The only responses a name server expects are replies to its own challenge queries.
            _challenge.OnResponse(in message, packet, clientAddress);
            return;
        }

        if (message.Header.QdCount != 1)
        {
            WinsCounters.Inc(ref _counters.Malformed);
            return;
        }

        if (message.HasScope)
        {
            WinsCounters.Inc(ref _counters.Unsupported);
            return;
        }

        bool broadcast = message.Header.IsBroadcast;
        if (broadcast && !_options.AnswerBroadcasts)
        {
            WinsCounters.Inc(ref _counters.IgnoredBroadcasts);
            return;
        }

        switch (message.Header.Opcode)
        {
            case NbnsOpcode.Query:
                HandleQuery(in message, clientAddress, clientPort, ctx, broadcast);
                break;

            // A broadcast registration/release is a B-node talking to its segment, not to a name
            // server - a server must neither record nor acknowledge it.
            case NbnsOpcode.Registration or NbnsOpcode.Refresh or NbnsOpcode.RefreshAlt or NbnsOpcode.MultihomedRegistration
                when !broadcast:
                HandleRegistration(in message, clientAddress, clientPort, ctx);
                break;

            case NbnsOpcode.Release when !broadcast:
                HandleRelease(in message, clientAddress, clientPort, ctx);
                break;

            default:
                WinsCounters.Inc(ref broadcast ? ref _counters.IgnoredBroadcasts : ref _counters.Unsupported);
                break;
        }
    }

    // ---------------------------------------------------------------- query

    private void HandleQuery(in NbnsMessage m, uint client, ushort port, WorkerContext ctx, bool broadcast)
    {
        // NBSTAT (node status) asks a node about ITSELF - "what names do you hold, what is your MAC"
        // (nbtstat -A, network scanners). Windows used to answer that on this address; with its
        // NetBIOS switched off, this server answers for the machine it runs on. Only unicast, and
        // only for the wildcard name or one of the machine's own names - never for other hosts.
        if (m.QuestionType != NbnsType.Nb)
        {
            if (m.QuestionType == NbnsType.NbStat && !broadcast && _self.NodeStatus is { } nodeStatus
                && (m.Name.IsWildcard || _self.Owns(m.Name)))
            {
                int statusLength = NbnsPackets.WriteNodeStatusResponse(ctx.Response, m.Header.TransactionId, m.Name, nodeStatus);
                Reply(ctx, statusLength, client, port);
                WinsCounters.Inc(ref _counters.NodeStatusReplies);
                _queryLog.Add(LogOp.Query, LogResult.NodeStatus, m.Name, client);
                return;
            }

            WinsCounters.Inc(ref _counters.Unsupported);
            return;
        }

        WinsCounters.Inc(ref _counters.Queries);
        long now = Clock.UnixNow();
        bool rd = m.Header.RecursionDesired;
        ushort id = m.Header.TransactionId;

        var outcome = _store.Query(m.Name, now, _options.MaxTtlSeconds, ctx.Addresses, out int count, out ushort nbFlags, out uint ttl);
        // Answer precedence: a static mapping always outranks a dynamic registration - also when
        // the static mapping lives on another WinsAlt server and the registration is local.
        //   local static > replicated static > local registration > replicated registration > partner > DNS
        if (outcome == QueryOutcome.Hit && _replicas.IsStatic(m.Name, now)
            && _replicas.Query(m.Name, now, _options.MaxTtlSeconds, ctx.Addresses, out int staticCount, out ushort staticFlags, out uint staticTtl))
        {
            int length = NbnsPackets.WriteNameResponse(ctx.Response, id, NbnsOpcode.Query, NbnsRcode.None, rd,
                m.Name, staticTtl, staticFlags, ctx.Addresses.AsSpan(0, staticCount));
            Reply(ctx, length, client, port);
            WinsCounters.Inc(ref _counters.ReplicaHits);
            _queryLog.Add(LogOp.Query, LogResult.ReplicaHit, m.Name, client, ctx.Addresses[0]);
            return;
        }

        if (outcome != QueryOutcome.Miss)
        {
            // Domain controllers (<1C>) exist at every site: add the ones the other servers hold.
            if (m.Name.Suffix == 0x1C) count = _replicas.AppendMembers(m.Name, now, ctx.Addresses, count);

            int length = NbnsPackets.WriteNameResponse(ctx.Response, id, NbnsOpcode.Query, NbnsRcode.None, rd,
                m.Name, ttl, nbFlags, ctx.Addresses.AsSpan(0, count));
            Reply(ctx, length, client, port);
            WinsCounters.Inc(ref _counters.QueryHits);
            _queryLog.Add(LogOp.Query, outcome == QueryOutcome.StaticHit ? LogResult.StaticHit : LogResult.Hit,
                m.Name, client, ctx.Addresses[0]);
            return;
        }

        // Not registered here - a name owned by another WinsAlt server (replica)?
        if (_replicas.Query(m.Name, now, _options.MaxTtlSeconds, ctx.Addresses, out count, out nbFlags, out ttl))
        {
            int length = NbnsPackets.WriteNameResponse(ctx.Response, id, NbnsOpcode.Query, NbnsRcode.None, rd,
                m.Name, ttl, nbFlags, ctx.Addresses.AsSpan(0, count));
            Reply(ctx, length, client, port);
            WinsCounters.Inc(ref _counters.ReplicaHits);
            _queryLog.Add(LogOp.Query, LogResult.ReplicaHit, m.Name, client, ctx.Addresses[0]);
            return;
        }

        // To a broadcast query a server only ever volunteers a positive answer - silence lets the
        // real owner on the segment respond; a negative reply would wrongly veto it.
        if (broadcast)
        {
            WinsCounters.Inc(ref _counters.QueryMisses);
            return;
        }

                // Not in the database: try the partner WINS servers, then DNS. The packet path only
        // reads the two caches; whatever is not cached yet is resolved on a task.
        // A query that itself comes from a partner is never forwarded back out (no loops).
        // A blocked name (WPAD, ISATAP...) is answered only from a static mapping - never from
        // whatever a partner server or DNS happens to hold for it.
        if (_options.IsBlockedName(m.Name))
        {
            int refused = NbnsPackets.WriteNegativeQueryResponse(ctx.Response, id, rd, m.Name, NbnsRcode.NameError);
            Reply(ctx, refused, client, port);
            WinsCounters.Inc(ref _counters.QueryMisses);
            _queryLog.Add(LogOp.Query, LogResult.Miss, m.Name, client);
            return;
        }

        bool askPartners = _partners.Enabled && !_partners.IsPartner(client);
        bool askDns = _dns.Enabled && DnsFallbackResolver.IsEligible(m.Name);
        bool unresolved = false;

        if (askPartners)
        {
            switch (_partners.TryGetCached(m.Name, now, ctx.Addresses, out int partnerCount, out ushort partnerFlags))
            {
                case DnsCacheState.Positive:
                    int length = NbnsPackets.WriteNameResponse(ctx.Response, id, NbnsOpcode.Query, NbnsRcode.None, rd,
                        m.Name, UpstreamTtlSeconds, partnerFlags, ctx.Addresses.AsSpan(0, partnerCount));
                    Reply(ctx, length, client, port);
                    WinsCounters.Inc(ref _counters.PartnerHits);
                    _queryLog.Add(LogOp.Query, LogResult.PartnerHit, m.Name, client, ctx.Addresses[0]);
                    return;

                case DnsCacheState.Unknown:
                    unresolved = true;
                    break;
            }
        }

        if (askDns && !unresolved)
        {
            switch (_dns.TryGetCached(m.Name, now, out uint cached))
            {
                case DnsCacheState.Positive:
                    ctx.Addresses[0] = cached;
                    int length = NbnsPackets.WriteNameResponse(ctx.Response, id, NbnsOpcode.Query, NbnsRcode.None, rd,
                        m.Name, UpstreamTtlSeconds, 0, ctx.Addresses.AsSpan(0, 1));
                    Reply(ctx, length, client, port);
                    WinsCounters.Inc(ref _counters.DnsHits);
                    _queryLog.Add(LogOp.Query, LogResult.DnsHit, m.Name, client, cached);
                    return;

                case DnsCacheState.Unknown:
                    unresolved = true;
                    break;
            }
        }

        if (unresolved)
        {
            _ = ResolveUpstreamAsync(ctx.Socket, id, rd, m.Name, client, port, askPartners, askDns, ctx.Stopping);
            return;
        }

        int negative = NbnsPackets.WriteNegativeQueryResponse(ctx.Response, id, rd, m.Name, NbnsRcode.NameError);
        Reply(ctx, negative, client, port);
        WinsCounters.Inc(ref _counters.QueryMisses);
        _queryLog.Add(LogOp.Query, LogResult.Miss, m.Name, client);
    }

    /// <summary>TTL handed to clients for answers that came from a partner or DNS (matches the upstream cache time).</summary>
    private const uint UpstreamTtlSeconds = 300;

    /// <summary>
    /// Slow path: wait for the partner servers, then DNS, and answer the client that is still
    /// waiting on its query. Both resolvers answer from their own cache when they can.
    /// </summary>
    private async Task ResolveUpstreamAsync(Socket socket, ushort id, bool rd, NameKey name, uint client, ushort port,
        bool askPartners, bool askDns, CancellationToken ct)
    {
        try
        {
            var buffer = new byte[NbnsPacketWriter.MaxPacket];
            int length;

            if (askPartners && await _partners.ResolveAsync(name, ct) is { } answer)
            {
                length = NbnsPackets.WriteNameResponse(buffer, id, NbnsOpcode.Query, NbnsRcode.None, rd,
                    name, UpstreamTtlSeconds, answer.NbFlags, answer.Addresses);
                WinsCounters.Inc(ref _counters.PartnerHits);
                _queryLog.Add(LogOp.Query, LogResult.PartnerHit, name, client, answer.Addresses[0]);
            }
            else if (askDns && await _dns.ResolveAsync(name, ct) is var address and not 0)
            {
                length = NbnsPackets.WriteNameResponse(buffer, id, NbnsOpcode.Query, NbnsRcode.None, rd,
                    name, UpstreamTtlSeconds, 0, [address]);
                WinsCounters.Inc(ref _counters.DnsHits);
                _queryLog.Add(LogOp.Query, LogResult.DnsHit, name, client, address);
            }
            else
            {
                length = NbnsPackets.WriteNegativeQueryResponse(buffer, id, rd, name, NbnsRcode.NameError);
                WinsCounters.Inc(ref _counters.QueryMisses);
                _queryLog.Add(LogOp.Query, LogResult.Miss, name, client);
            }
            await SendAsync(socket, buffer, length, client, port, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Upstream lookup reply failed for {Name}", name);
        }
    }

    // ---------------------------------------------------------------- registration / refresh

    private void HandleRegistration(in NbnsMessage m, uint client, ushort port, WorkerContext ctx)
    {
        var opcode = m.Header.Opcode;
        bool refresh = opcode is NbnsOpcode.Refresh or NbnsOpcode.RefreshAlt;
        var op = refresh ? LogOp.Refresh : LogOp.Register;
        bool rd = m.Header.RecursionDesired;
        ushort id = m.Header.TransactionId;

        if (!m.HasNbRecord || m.Name.IsWildcard)
        {
            ReplyRecord(ctx, id, opcode, NbnsRcode.FormatError, rd, m.Name, 0, 0, 0, client, port);
            WinsCounters.Inc(ref _counters.Malformed);
            return;
        }

        // The address being registered is the one in the record; hosts behind this server's own
        // subnet always put their real address there. 0.0.0.0 falls back to the packet source.
        uint address = m.Address != 0 ? m.Address : client;
        ushort nbFlags = m.NbFlags;
        var kind = opcode == NbnsOpcode.MultihomedRegistration ? NameKind.Multihomed
            : (nbFlags & NbFlags.Group) != 0 ? NameKind.Group
            : NameKind.Unique;
        uint ttl = Math.Clamp(m.Ttl == 0 ? _options.MaxTtlSeconds : m.Ttl, _options.MinTtlSeconds, _options.MaxTtlSeconds);

        long now = Clock.UnixNow();

        // Registration policy first: who may register at all, and which names never may be.
        if (CheckPolicy(m.Name, address, client, now, registering: true) is var verdict and not PolicyVerdict.Allowed)
        {
            RefuseByPolicy(ctx, in m, op, verdict, address, client, port);
            return;
        }

        // 0x1D (segment master browser) is only meaningful per broadcast segment. WINS
        // acknowledges the registration but never stores or resolves it.
        if (m.Name.Suffix == 0x1D)
        {
            ReplyRecord(ctx, id, opcode, NbnsRcode.None, rd, m.Name, ttl, nbFlags, address, client, port);
            _queryLog.Add(op, LogResult.Ignored, m.Name, client, address);
            return;
        }

        // A static mapping on another WinsAlt server protects its name here exactly as a local one
        // does: the host it names is acknowledged, anyone else is refused - no challenge needed.
        if (!_store.IsStaticName(m.Name) && _replicas.TryGet(m.Name, out var foreign) && foreign.IsStatic && foreign.IsActive(now))
        {
            bool isTheMappedHost = (kind == NameKind.Group && foreign.Kind == NameKind.Group)
                                   || Array.Exists(foreign.Members, member => member.Address == address);
            if (isTheMappedHost)
            {
                ReplyRecord(ctx, id, opcode, NbnsRcode.None, rd, m.Name, ttl, nbFlags, address, client, port);
                WinsCounters.Inc(ref _counters.Refreshes);
                _queryLog.Add(op, LogResult.Refreshed, m.Name, client, address);
            }
            else Deny(ctx, in m, op, LogResult.Denied, address, client, port, $"static mapping on {foreign.Origin}");
            return;
        }

        // A unique name that another WinsAlt server owns (a replica here) is not free for the
        // taking just because this server has no local record of it: treat it as a conflict with
        // the address the replica names, exactly like a local one - the owner is challenged.
        if (kind != NameKind.Group && !_store.HasActive(m.Name, now)
            && _replicas.TryGetForeignOwner(m.Name, now, address, out uint replicaOwner))
        {
            ResolveConflict(ctx, in m, op, kind, address, ttl, replicaOwner, client, port);
            return;
        }

        var outcome = _store.Register(m.Name, address, nbFlags, kind, now, ttl, out uint owner);
        switch (outcome)
        {
            case RegisterOutcome.QuotaExceeded:
                RefuseByPolicy(ctx, in m, op, PolicyVerdict.Quota, address, client, port);
                break;

            case RegisterOutcome.Registered:
                ReplyRecord(ctx, id, opcode, NbnsRcode.None, rd, m.Name, ttl, nbFlags, address, client, port);
                WinsCounters.Inc(ref _counters.Registrations);
                _queryLog.Add(op, LogResult.Registered, m.Name, client, address);
                if (_logger.IsEnabled(LogLevel.Information) && _infoGate.Allow())
                    _logger.LogInformation("Registered {Name} -> {Address} ({Kind}, ttl {Ttl}s)", m.Name, Ipv4.ToString(address), kind, ttl);
                break;

            case RegisterOutcome.Refreshed:
            case RegisterOutcome.StaticMatch:
                ReplyRecord(ctx, id, opcode, NbnsRcode.None, rd, m.Name, ttl, nbFlags, address, client, port);
                WinsCounters.Inc(ref _counters.Refreshes);
                _queryLog.Add(op, LogResult.Refreshed, m.Name, client, address);
                break;

            case RegisterOutcome.Denied:
                Deny(ctx, in m, op, LogResult.Denied, address, client, port, "static mapping or unique/group clash");
                break;

            case RegisterOutcome.Conflict:
                ResolveConflict(ctx, in m, op, kind, address, ttl, owner, client, port);
                break;
        }
    }

    private void ResolveConflict(WorkerContext ctx, in NbnsMessage m, LogOp op, NameKind kind, uint address, uint ttl,
        uint owner, uint client, ushort port)
    {
        var opcode = m.Header.Opcode;
        bool rd = m.Header.RecursionDesired;
        ushort id = m.Header.TransactionId;

        switch (_options.ConflictPolicy)
        {
            case ConflictPolicy.Reject:
                Deny(ctx, in m, op, LogResult.Conflict, address, client, port, $"held by {Ipv4.ToString(owner)}");
                break;

            case ConflictPolicy.Overwrite:
                _store.ForceRegister(m.Name, address, m.NbFlags, kind, Clock.UnixNow(), ttl);
                ReplyRecord(ctx, id, opcode, NbnsRcode.None, rd, m.Name, ttl, m.NbFlags, address, client, port);
                WinsCounters.Inc(ref _counters.Registrations);
                _queryLog.Add(op, LogResult.Registered, m.Name, client, address);
                if (_infoGate.Allow())
                    _logger.LogInformation("Registered {Name} -> {Address} (replaced {Owner})", m.Name, Ipv4.ToString(address), Ipv4.ToString(owner));
                break;

            default:
                // One challenge per name at a time; a retransmitted request is simply dropped
                // (the client is already holding on the WACK below).
                if (!_challenge.TryBegin(m.Name)) return;

                int length = NbnsPackets.WriteWack(ctx.Response, id, m.Name, NameChallengeService.WackSeconds, m.Header.Flags);
                Reply(ctx, length, client, port);
                _queryLog.Add(op, LogResult.Challenging, m.Name, client, owner);
                _ = ChallengeAsync(ctx.Socket, id, opcode, rd, m.Name, address, m.NbFlags, kind, ttl, owner, client, port, ctx.Stopping);
                break;
        }
    }

    /// <summary>Slow path: challenge the current owner, then give the held client its final answer.</summary>
    private async Task ChallengeAsync(Socket socket, ushort id, NbnsOpcode opcode, bool rd, NameKey name, uint address,
        ushort nbFlags, NameKind kind, uint ttl, uint owner, uint client, ushort port, CancellationToken ct)
    {
        var op = opcode is NbnsOpcode.Refresh or NbnsOpcode.RefreshAlt ? LogOp.Refresh : LogOp.Register;
        try
        {
            var result = await _challenge.ChallengeAsync(socket, name, owner, address, ct);
            // With merging switched off, an owner that answers keeps its name - full stop.
            if (result == ChallengeResult.SameHost && !_options.AllowMultihomedMerge) result = ChallengeResult.Defended;
            bool sameHost = result == ChallengeResult.SameHost;
            bool granted = result switch
            {
                // Same machine on a second adapter: keep the first address and add this one.
                ChallengeResult.SameHost => _store.AddOwnAddress(name, address, nbFlags, Clock.UnixNow(), ttl),
                ChallengeResult.NotDefended => _store.ForceRegister(name, address, nbFlags, kind, Clock.UnixNow(), ttl),
                _ => false
            };

            var buffer = new byte[NbnsHeader.Size + NbnsName.EncodedLength + 16];
            int length = NbnsPackets.WriteNameResponse(buffer, id, opcode, granted ? NbnsRcode.None : NbnsRcode.Active, rd,
                name, granted ? ttl : 0, nbFlags, [address]);
            await SendAsync(socket, buffer, length, client, port, ct);

            if (granted)
            {
                WinsCounters.Inc(ref _counters.Registrations);
                _queryLog.Add(op, LogResult.Registered, name, client, address);
                if (sameHost)
                    _logger.LogInformation("Registered {Name} -> {Address} as a second address of the same machine (also at {Owner})",
                        name, Ipv4.ToString(address), Ipv4.ToString(owner));
                else
                    _logger.LogInformation("Registered {Name} -> {Address} (previous owner {Owner} did not defend it)",
                        name, Ipv4.ToString(address), Ipv4.ToString(owner));
            }
            else
            {
                WinsCounters.Inc(ref _counters.Conflicts);
                _queryLog.Add(op, LogResult.Conflict, name, client, owner);
                if (_warnGate.Allow())
                    _logger.LogWarning("Name conflict: {Address} tried to register {Name}, still defended by {Owner}",
                        Ipv4.ToString(address), name, Ipv4.ToString(owner));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Name challenge failed for {Name}", name);
        }
        finally
        {
            _challenge.End(name);
        }
    }

    private void Deny(WorkerContext ctx, in NbnsMessage m, LogOp op, LogResult result, uint address, uint client, ushort port, string reason)
    {
        ReplyRecord(ctx, m.Header.TransactionId, m.Header.Opcode, NbnsRcode.Active, m.Header.RecursionDesired,
            m.Name, 0, m.NbFlags, address, client, port);
        WinsCounters.Inc(ref _counters.Conflicts);
        _queryLog.Add(op, result, m.Name, client, address);
        if (_warnGate.Allow())
            _logger.LogWarning("Refused registration of {Name} from {Address}: {Reason}", m.Name, Ipv4.ToString(address), reason);
    }

    // ---------------------------------------------------------------- registration policy

    private enum PolicyVerdict { Allowed, BlockedName, BogusAddress, StaticOnly, OutsideAllowedSubnets, AddressMismatch, Quota }

    /// <summary>
    /// The anti-poisoning rules for dynamic registration (and release). Allocation-free: a hash
    /// probe for the blocked-name list, then at most a few AND/compare pairs for the subnets.
    ///  1. Blocked names (WPAD, ISATAP by default - the names poisoning tools claim) can never be
    ///     registered dynamically, whoever asks.
    ///  2. StaticOnly: nothing is registered dynamically. A host announcing a name that a static
    ///     mapping already gives it is still acknowledged, so correctly configured machines see no error.
    ///  3. AllowedSubnets: both the sender and the address being registered must be inside a
    ///     configured network.
    ///  4. RequireAddressMatchesSource: a host may only register its own address.
    /// </summary>
    private PolicyVerdict CheckPolicy(NameKey name, uint address, uint client, long now, bool registering)
    {
        if (registering && _options.IsBlockedName(name)) return PolicyVerdict.BlockedName;

        // A name must point at a real host address: never 0.x, multicast/reserved/broadcast, and
        // loopback only from the loopback interface itself (a local test) - storing such a value
        // would poison every client that later resolves the name.
        if (registering && !Ipv4.IsUsableHost(address, allowLoopback: client >> 24 == 127)) return PolicyVerdict.BogusAddress;

        switch (_options.RegistrationMode)
        {
            case RegistrationMode.StaticOnly:
                if (!_store.IsStaticFor(name, address, now)) return PolicyVerdict.StaticOnly;
                break;

            case RegistrationMode.AllowedSubnets:
                if (!InAllowedSubnet(client) || !InAllowedSubnet(address)) return PolicyVerdict.OutsideAllowedSubnets;
                break;
        }

        return _options.RequireAddressMatchesSource && address != client ? PolicyVerdict.AddressMismatch : PolicyVerdict.Allowed;
    }

    private bool InAllowedSubnet(uint address)
    {
        foreach (var network in _options.AllowedSubnets)
            if (network.Contains(address)) return true;
        return false;
    }

    /// <summary>Answers RFS_ERR ("refused for policy reasons", RFC 1002 RCODE 5) and records why.</summary>
    private void RefuseByPolicy(WorkerContext ctx, in NbnsMessage m, LogOp op, PolicyVerdict verdict, uint address, uint client, ushort port)
    {
        ReplyRecord(ctx, m.Header.TransactionId, m.Header.Opcode, NbnsRcode.Refused, m.Header.RecursionDesired,
            m.Name, 0, m.NbFlags, address, client, port);

        bool blocked = verdict == PolicyVerdict.BlockedName;
        WinsCounters.Inc(ref blocked ? ref _counters.BlockedNames : ref _counters.RefusedByPolicy);
        _queryLog.Add(op, blocked ? LogResult.Blocked : LogResult.Refused, m.Name, client, address);

        if (_warnGate.Allow())
            _logger.LogWarning("Refused {Op} of {Name} -> {Address} from {Client}: {Reason}", op, m.Name, Ipv4.ToString(address),
                Ipv4.ToString(client), verdict switch
                {
                    PolicyVerdict.BlockedName => "the name is on the blocked list",
                    PolicyVerdict.BogusAddress => "the address is not a usable host address",
                    PolicyVerdict.StaticOnly => "dynamic registration is switched off (StaticOnly)",
                    PolicyVerdict.OutsideAllowedSubnets => "sender or address is outside the allowed subnets",
                    PolicyVerdict.AddressMismatch => "the registered address is not the sender's own",
                    _ => "the name quota for this address or for the database is full"
                });
    }

    // ---------------------------------------------------------------- release

    private void HandleRelease(in NbnsMessage m, uint client, ushort port, WorkerContext ctx)
    {
        bool rd = m.Header.RecursionDesired;
        ushort id = m.Header.TransactionId;

        if (!m.HasNbRecord)
        {
            ReplyRecord(ctx, id, NbnsOpcode.Release, NbnsRcode.FormatError, rd, m.Name, 0, 0, 0, client, port);
            WinsCounters.Inc(ref _counters.Malformed);
            return;
        }

        uint address = m.Address != 0 ? m.Address : client;
        long now = Clock.UnixNow();
        if (_options.RegistrationMode != RegistrationMode.StaticOnly
            && CheckPolicy(m.Name, address, client, now, registering: false) is var verdict and not PolicyVerdict.Allowed)
        {
            RefuseByPolicy(ctx, in m, LogOp.Release, verdict, address, client, port);
            return;
        }

        var outcome = _store.Release(m.Name, address, now);

        // Releasing a name that is not registered (or is static) is acknowledged: the client's
        // goal - "I no longer hold this name" - is already true. Only releasing someone else's
        // name is an error.
        var rcode = outcome == ReleaseOutcome.NotOwner ? NbnsRcode.Active : NbnsRcode.None;
        ReplyRecord(ctx, id, NbnsOpcode.Release, rcode, rd, m.Name, 0, m.NbFlags, address, client, port);

        switch (outcome)
        {
            case ReleaseOutcome.Released:
                WinsCounters.Inc(ref _counters.Releases);
                _queryLog.Add(LogOp.Release, LogResult.Released, m.Name, client, address);
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation("Released {Name} ({Address})", m.Name, Ipv4.ToString(address));
                break;
            case ReleaseOutcome.NotOwner:
                _queryLog.Add(LogOp.Release, LogResult.NotOwner, m.Name, client, address);
                break;
            default:
                _queryLog.Add(LogOp.Release, LogResult.Ignored, m.Name, client, address);
                break;
        }
    }

    // ---------------------------------------------------------------- send helpers

    /// <summary>Writes and sends a one-address NB record response (registration / refresh / release).</summary>
    private void ReplyRecord(WorkerContext ctx, ushort id, NbnsOpcode opcode, NbnsRcode rcode, bool rd, NameKey name,
        uint ttl, ushort nbFlags, uint address, uint client, ushort port)
    {
        ctx.Addresses[0] = address;
        int length = NbnsPackets.WriteNameResponse(ctx.Response, id, opcode, rcode, rd, name, ttl, nbFlags, ctx.Addresses.AsSpan(0, 1));
        Reply(ctx, length, client, port);
    }

    private void Reply(WorkerContext ctx, int length, uint client, ushort port)
    {
        if (ctx.Send(length, client, port)) WinsCounters.Inc(ref _counters.ResponsesSent);
    }

    private async ValueTask SendAsync(Socket socket, byte[] buffer, int length, uint client, ushort port, CancellationToken ct)
    {
        try
        {
            await socket.SendToAsync(buffer.AsMemory(0, length), SocketFlags.None, new IPEndPoint(Ipv4.ToIPAddress(client), port), ct);
            WinsCounters.Inc(ref _counters.ResponsesSent);
        }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }
}
