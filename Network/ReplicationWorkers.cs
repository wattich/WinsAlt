using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using WinsAlt.Infrastructure;

namespace WinsAlt.Network;

/// <summary>
/// Answers replication pulls from peer WinsAlt servers (TCP, default port 8138).
///
/// A connection gets a fresh nonce, must present one correctly authenticated request, and
/// receives one snapshot of the names this server owns. The exchange is read-only: nothing a
/// peer sends can change this server's database. Without a replication key nothing is answered.
/// </summary>
public sealed class ReplicationServer : BackgroundService
{
    private const int RetryIntervalSeconds = 15;
    private const int MaxRequestBytes = 4096;
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(15);
    // How long an unauthenticated connection may sit before sending its request (anti-slowloris).
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly ReplicationService _replication;
    private readonly ILogger<ReplicationServer> _logger;
    private readonly SemaphoreSlim _slots = new(8);

    public ReplicationServer(ReplicationService replication, ILogger<ReplicationServer> logger)
    {
        _replication = replication;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Same self-heal pattern as the NBNS listener: a busy port must not take the service down.
        int attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var listener = new TcpListener(IPAddress.Any, _replication.Port);
            try
            {
                if (OperatingSystem.IsWindows()) listener.Server.ExclusiveAddressUse = true;
                listener.Start();
                _replication.ListenerState = "Listening";
                _replication.ListenerError = null;
                attempt = 0;

                while (true)
                {
                    var client = await listener.AcceptTcpClientAsync(stoppingToken);
                    _ = ServeAsync(client, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _replication.ListenerState = "Error";
                _replication.ListenerError = ex.Message;
                if (attempt++ == 0)
                    _logger.LogError("Replication listener on TCP {Port} failed: {Error} - retrying every {Seconds}s",
                        _replication.Port, ex.Message, RetryIntervalSeconds);
            }
            finally
            {
                listener.Stop();
            }

            try { await Task.Delay(TimeSpan.FromSeconds(RetryIntervalSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        _replication.ListenerState = "Stopped";
    }

    private async Task ServeAsync(TcpClient client, CancellationToken stopping)
    {
        using (client)
        {
            // Only the servers configured as peers may even start a conversation: everyone else is
            // dropped before a single byte is sent, so the port gives nothing to scan or to brute-force.
            if (client.Client.RemoteEndPoint is not IPEndPoint remote || !_replication.IsPeer(remote.Address)) return;

            // No key = replication is off; too many parallel pulls = let the peer retry later.
            if (_replication.KeyBytes is not { } key || !await _slots.WaitAsync(0, stopping)) return;

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                timeout.CancelAfter(ExchangeTimeout);
                var stream = client.GetStream();

                byte[] nonce = RandomNumberGenerator.GetBytes(ReplicationWire.NonceLength);
                await stream.WriteAsync(nonce, timeout.Token);

                // A request that does not authenticate gets no answer at all - just a closed connection.
                byte[]? request;
                using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
                {
                    waiting.CancelAfter(RequestTimeout);
                    request = await ReplicationWire.ReadFrameAsync(stream, key, nonce, ReplicationWire.Request, MaxRequestBytes, waiting.Token);
                }
                if (request is null)
                {
                    _logger.LogWarning("Rejected a replication request from {Remote}: wrong replication key", client.Client.RemoteEndPoint);
                    return;
                }

                await ReplicationWire.WriteFrameAsync(stream, _replication.BuildSnapshot(), key, nonce, ReplicationWire.Response, timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or EndOfStreamException)
            {
                // Peer went away mid-exchange - it will simply pull again.
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Replication request failed");
            }
            finally
            {
                _slots.Release();
            }
        }
    }
}

/// <summary>
/// Pulls a snapshot from every enabled replication peer on a timer (and at once when the peer
/// list or the key changes).
/// </summary>
public sealed class ReplicationClient : BackgroundService
{
    private static readonly TimeSpan PullInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(30);
    private const int MaxSnapshotBytes = 16 * 1024 * 1024;

    private readonly ReplicationService _replication;

    public ReplicationClient(ReplicationService replication) => _replication = replication;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var peers = _replication.EnabledPeers();
                await Task.WhenAll(peers.Select(peer => PullAsync(peer, stoppingToken)));
                await _replication.Changed.WaitAsync(PullInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task PullAsync(ReplicationPeer peer, CancellationToken stopping)
    {
        if (_replication.KeyBytes is not { } key)
        {
            _replication.ReportFailure(peer, "No replication key is set on this server.");
            return;
        }

        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(stopping))
            {
                connect.CancelAfter(ConnectTimeout);
                await client.ConnectAsync(IPAddress.Parse(peer.Address), peer.Port, connect.Token);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            timeout.CancelAfter(ExchangeTimeout);
            var stream = client.GetStream();

            var nonce = new byte[ReplicationWire.NonceLength];
            await stream.ReadExactlyAsync(nonce, timeout.Token);
            await ReplicationWire.WriteFrameAsync(stream, _replication.BuildRequest(), key, nonce, ReplicationWire.Request, timeout.Token);

            var body = await ReplicationWire.ReadFrameAsync(stream, key, nonce, ReplicationWire.Response, MaxSnapshotBytes, timeout.Token);
            if (body is null)
            {
                _replication.ReportFailure(peer, "The answer could not be verified - the replication key differs between the two servers.");
                return;
            }

            if (_replication.ApplySnapshot(peer, body) is { } error) _replication.ReportFailure(peer, error);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _replication.ReportFailure(peer, $"No answer from {peer.Address}:{peer.Port} (timed out) - is TCP {peer.Port} open in its firewall?");
        }
        catch (EndOfStreamException)
        {
            // The server closes without a word when the caller is not one of its peers, when the request does not
            // authenticate, or when it has no key.
            _replication.ReportFailure(peer, "The peer closed the connection - this server is not in its server list yet, or its replication key is missing or different.");
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            _replication.ReportFailure(peer, $"Cannot reach {peer.Address}:{peer.Port} - {ex.Message}");
        }
        catch (Exception ex)
        {
            _replication.ReportFailure(peer, ex.Message);
        }
    }
}
