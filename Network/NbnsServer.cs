using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using WinsAlt.Core.Domain;
using WinsAlt.Core.Protocol;
using WinsAlt.Infrastructure;

namespace WinsAlt.Network;

public enum ListenerState { Starting, Active, Error, Stopped }

/// <summary>
/// The NetBIOS Name Server listener (UDP 137).
///
/// Pipeline:
///   1 receive loop  ->  N bounded channels  ->  N workers (parse, resolve, reply)
///
///  - The receive loop does nothing but pull datagrams off the socket into pooled buffers, so a
///    burst is absorbed by the socket buffer + channels instead of being dropped by the kernel
///    while a worker is busy.
///  - A datagram is routed to a worker by a hash of its source address. Packets from one client
///    are therefore always handled in arrival order (register-then-query never races), while
///    different clients spread across cores.
///  - Channels are bounded and the writer never waits: when a worker's queue is full the datagram
///    is dropped and counted. NBNS clients retransmit, so shedding load beats queueing without
///    limit and answering after the client has already timed out.
///
/// Steady state allocates nothing per packet: buffers come from <see cref="ArrayPool{T}"/>, the
/// source address is read out of one reused <see cref="SocketAddress"/>, the channel item is a
/// struct, and each worker owns its response buffer.
/// </summary>
public sealed class NbnsServer : BackgroundService
{
    private readonly record struct Datagram(byte[] Buffer, int Length, uint Address, ushort Port);

    // RFC 1002 limits NBNS datagrams to 576 bytes; anything larger is not a name service packet.
    private const int ReceiveBufferSize = 1024;
    private const int RetryIntervalSeconds = 15;

    private readonly NbnsRequestHandler _handler;
    private readonly PartnerService _partners;
    private readonly WinsCounters _counters;
    private readonly WinsOptions _options;
    private readonly ILogger<NbnsServer> _logger;
    private readonly SemaphoreSlim _retryNow = new(0);
    private long _startedAt;

    public NbnsServer(NbnsRequestHandler handler, PartnerService partners, WinsCounters counters, WinsOptions options, ILogger<NbnsServer> logger)
    {
        _handler = handler;
        _partners = partners;
        _counters = counters;
        _options = options;
        _logger = logger;
        EndPoint = new IPEndPoint(options.ListenAddress, options.Port);
    }

    public IPEndPoint EndPoint { get; }
    public ListenerState State { get; private set; } = ListenerState.Starting;
    public string? LastError { get; private set; }
    public long StartedAt => Volatile.Read(ref _startedAt);
    public int Workers => _options.Workers;

    /// <summary>Cuts the wait before the next bind attempt short (e.g. right after NetBT was switched off).</summary>
    public void RetryNow()
    {
        if (State == ListenerState.Error && _retryNow.CurrentCount == 0) _retryNow.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Self-heal: keep retrying while the listener cannot bind. Typical causes are a boot-time
        // race (the bound IP is not on the NIC yet) and - on Windows - the NetBT driver still
        // owning UDP 137 on that adapter. Log the first failure loudly, the rest at Debug.
        int attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunListenerAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                State = ListenerState.Error;
                LastError = ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse or SocketError.AccessDenied }
                    ? $"{ex.Message} (UDP {EndPoint} is in use - " + (OperatingSystem.IsWindows()
                        ? "disable NetBIOS over TCP/IP on this adapter)"
                        : ex is SocketException { SocketErrorCode: SocketError.AccessDenied }
                            ? "ports below 1024 need root or CAP_NET_BIND_SERVICE)"
                            : "Samba's nmbd usually holds it: systemctl disable --now nmbd)")
                    : ex.Message;

                if (attempt++ == 0)
                    _logger.LogError("NBNS listener on {EndPoint} failed: {Error} - retrying every {Seconds}s", EndPoint, LastError, RetryIntervalSeconds);
                else
                    _logger.LogDebug("NBNS listener retry {Attempt} failed: {Error}", attempt, ex.Message);
            }

            try { await _retryNow.WaitAsync(TimeSpan.FromSeconds(RetryIntervalSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        if (State != ListenerState.Error) State = ListenerState.Stopped;
    }

    private async Task RunListenerAsync(CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        DisableConnReset(socket);
        socket.ReceiveBufferSize = 1 << 20; // ride out bursts (e.g. a whole site booting at 9:00)
        // Without this, Windows lets a 0.0.0.0:137 bind succeed while the NetBT driver still holds
        // <each-ip>:137. The listener then looks healthy but only ever sees broadcasts: every
        // unicast packet (i.e. every real WINS request) goes to NetBT's more specific socket.
        // Exclusive use makes that bind fail instead, so the conflict is reported, not hidden.
        // (Linux shares a port only between sockets that both ask for SO_REUSEADDR, so it is exclusive already.)
        if (OperatingSystem.IsWindows()) socket.ExclusiveAddressUse = true;
        socket.Bind(EndPoint);

        int workerCount = _options.Workers;
        var channels = new Channel<Datagram>[workerCount];
        var workers = new Task[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            channels[i] = Channel.CreateBounded<Datagram>(new BoundedChannelOptions(_options.QueueCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait // never awaited: TryWrite fails fast when full
            });
            var reader = channels[i].Reader;
            workers[i] = Task.Run(() => WorkerLoopAsync(reader, socket, ct), CancellationToken.None);
        }

        State = ListenerState.Active;
        LastError = null;
        Volatile.Write(ref _startedAt, Clock.UnixNow());
        _logger.LogInformation("NBNS listener active on UDP {EndPoint} ({Workers} worker(s))", EndPoint, workerCount);

        try
        {
            await ReceiveLoopAsync(socket, channels, ct);
        }
        finally
        {
            foreach (var channel in channels) channel.Writer.TryComplete();
            await Task.WhenAll(workers);
        }
    }

    private async Task ReceiveLoopAsync(Socket socket, Channel<Datagram>[] channels, CancellationToken ct)
    {
        // One SocketAddress reused for every receive - the IPEndPoint-returning overloads
        // allocate an endpoint (and an IPAddress) per datagram.
        var remote = new SocketAddress(AddressFamily.InterNetwork);
        byte[]? buffer = null;
        // Owned by this loop alone (single reader of the socket), so it needs no synchronisation.
        var limiter = _options.RateLimitPerSecond > 0 ? new SourceRateLimiter(_options.RateLimitPerSecond, _options.RateLimitBurst) : null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                buffer ??= ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);

                int length;
                try
                {
                    length = await socket.ReceiveFromAsync(buffer.AsMemory(0, ReceiveBufferSize), SocketFlags.None, remote, ct);
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.MessageSize or SocketError.ConnectionReset)
                {
                    // Oversized datagram (not NBNS) or a stray ICMP unreachable - keep listening.
                    WinsCounters.Inc(ref _counters.Malformed);
                    continue;
                }

                WinsCounters.Inc(ref _counters.PacketsReceived);
                if (length < NbnsHeader.Size)
                {
                    WinsCounters.Inc(ref _counters.Malformed);
                    continue;
                }

                // RFC 1002 §4.2: an NBNS datagram is at most 576 bytes. Anything longer is not
                // name-service traffic and is dropped unparsed.
                if (length > NbnsPacketWriter.MaxPacket)
                {
                    WinsCounters.Inc(ref _counters.Oversized);
                    continue;
                }

                // sockaddr_in: family(2) port(2, network order) addr(4, network order)
                ReadOnlySpan<byte> sockaddr = remote.Buffer.Span;
                ushort port = BinaryPrimitives.ReadUInt16BigEndian(sockaddr[2..]);
                uint address = BinaryPrimitives.ReadUInt32BigEndian(sockaddr[4..]);

                // Flood guard, before the packet costs a queue slot or a parse. Responses (R bit set)
                // are exempt - they are answers to this server's own challenge queries - and so are
                // partner name servers, which legitimately speak for many clients.
                if (limiter is not null && (buffer[2] & 0x80) == 0 && !_partners.IsPartner(address)
                    && !limiter.Allow(address, Environment.TickCount64))
                {
                    WinsCounters.Inc(ref _counters.RateLimited);
                    continue;
                }

                // Fibonacci hash of the source address -> worker index (same client, same worker).
                int worker = (int)((address * 2654435761u) >> 16) % channels.Length;
                if (channels[worker].Writer.TryWrite(new Datagram(buffer, length, address, port)))
                    buffer = null; // ownership moved to the worker, which returns it to the pool
                else
                    WinsCounters.Inc(ref _counters.Dropped);
            }
        }
        finally
        {
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task WorkerLoopAsync(ChannelReader<Datagram> reader, Socket socket, CancellationToken ct)
    {
        var context = new WorkerContext(socket, ct);

        // Drains until the writer completes (not until cancellation), so every buffer handed to
        // this worker is returned to the pool on shutdown.
        while (await reader.WaitToReadAsync(CancellationToken.None))
        {
            while (reader.TryRead(out var datagram))
            {
                try
                {
                    if (!ct.IsCancellationRequested)
                        _handler.Process(datagram.Buffer.AsSpan(0, datagram.Length), datagram.Address, datagram.Port, context);
                }
                catch (Exception ex)
                {
                    // A handler bug must not take the worker (and a share of all clients) down.
                    _logger.LogError(ex, "Unhandled error processing an NBNS packet from {Address}", Ipv4.ToString(datagram.Address));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(datagram.Buffer);
                }
            }
        }
    }

    private void DisableConnReset(Socket socket)
    {
        // SIO_UDP_CONNRESET = 0x9800000C - stops Windows from failing the next receive with
        // WSAECONNRESET after one of our replies drew an ICMP "port unreachable".
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            const int SIO_UDP_CONNRESET = -1744830452; // unchecked((int)0x9800000C)
            socket.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("SIO_UDP_CONNRESET not applied: {Msg}", ex.Message);
        }
    }
}
