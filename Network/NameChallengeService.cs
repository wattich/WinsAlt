using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using WinsAlt.Core.Domain;
using WinsAlt.Core.Protocol;

namespace WinsAlt.Network;

/// <summary>
/// Name challenge (RFC 1002 §5.1.4.x, what WINS does on a unique-name conflict): before giving a
/// name to a new address, ask the address that currently holds it. If it still answers a name
/// query, it keeps the name; if it stays silent or disowns the name, the newcomer gets it. This
/// is what lets a host that changed IP (DHCP) re-register immediately, without letting an
/// arbitrary host take over a name that is still in use.
///
/// The challenge query is sent from the server's own port-137 socket, so the owner's reply
/// arrives through the normal receive path and is routed back here by transaction id.
/// </summary>
public sealed class NameChallengeService
{
    private sealed class Pending(NameKey name, uint owner, uint claimant)
    {
        public readonly NameKey Name = name;
        public readonly uint Owner = owner;
        public readonly uint Claimant = claimant;
        public readonly TaskCompletionSource<ChallengeResult> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private const int Attempts = 3;
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>Seconds a challenged client is told to wait (WACK TTL) - covers every attempt.</summary>
    public const uint WackSeconds = 3;

    private readonly ConcurrentDictionary<ushort, Pending> _pending = new();
    private readonly ConcurrentDictionary<NameKey, byte> _challenging = new();
    private readonly int _port;
    private readonly int _maxConcurrent;

    public NameChallengeService(WinsOptions options)
    {
        _port = options.ChallengePort;
        _maxConcurrent = options.MaxConcurrentChallenges;
    }

    /// <summary>
    /// Claims the name for one challenge at a time; duplicates (client retransmits) are dropped.
    /// The total in flight is capped too: every challenge makes this server send packets to a
    /// third party (the owner) and a WACK to the claimant, so an unbounded number would let
    /// forged registrations use it as a traffic reflector. Over the cap the request is dropped
    /// and the client's own retransmission tries again.
    /// </summary>
    public bool TryBegin(NameKey name) => _challenging.Count < _maxConcurrent && _challenging.TryAdd(name, 0);

    public void End(NameKey name) => _challenging.TryRemove(name, out _);

    /// <summary>Called from the receive path for every response packet (R=1).</summary>
    public void OnResponse(in NbnsMessage message, ReadOnlySpan<byte> packet, uint fromAddress)
    {
        if (message.Header.Opcode != NbnsOpcode.Query) return;
        if (!_pending.TryGetValue(message.Header.TransactionId, out var pending)) return;
        if (pending.Owner != fromAddress || pending.Name != message.Name) return;

        // Positive response = "this name is mine"; a negative one = the owner no longer wants it.
        bool positive = message.Header.Rcode == NbnsRcode.None && message.Header.AnCount > 0;
        pending.Reply.TrySetResult(
            !positive ? ChallengeResult.NotDefended
            : message.ListsAddress(packet, pending.Claimant) ? ChallengeResult.SameHost
            : ChallengeResult.Defended);
    }

    /// <summary>Asks the current owner whether it still holds the name that <paramref name="claimantAddress"/> wants.</summary>
    public async Task<ChallengeResult> ChallengeAsync(Socket socket, NameKey name, uint ownerAddress, uint claimantAddress, CancellationToken ct)
    {
        var query = new byte[NbnsHeader.Size + NbnsName.EncodedLength + 4];
        var ownerEndPoint = new IPEndPoint(Ipv4.ToIPAddress(ownerAddress), _port);

        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            // Unpredictable per attempt: the reply is matched on (owner address, id, name), and a
            // counter would let anyone who has seen one challenge forge the answer to the next.
            ushort id = (ushort)System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, 65536);
            var pending = new Pending(name, ownerAddress, claimantAddress);
            if (!_pending.TryAdd(id, pending)) continue;

            try
            {
                int length = NbnsPackets.WriteNameQuery(query, id, name);
                await socket.SendToAsync(query.AsMemory(0, length), SocketFlags.None, ownerEndPoint, ct);

                var finished = await Task.WhenAny(pending.Reply.Task, Task.Delay(AttemptTimeout, ct));
                ct.ThrowIfCancellationRequested();
                if (finished == pending.Reply.Task) return pending.Reply.Task.Result;
            }
            catch (SocketException)
            {
                // Owner unreachable (no route / host down) - same as no answer.
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }
        return ChallengeResult.NotDefended;
    }
}

public enum ChallengeResult
{
    /// <summary>The owner did not answer (or disowned the name) - the newcomer may have it.</summary>
    NotDefended,
    /// <summary>The owner answered and still holds the name; the newcomer is a different machine.</summary>
    Defended,
    /// <summary>
    /// The owner answered and its answer lists the newcomer's address too: both addresses belong
    /// to one multihomed machine (typically a laptop on LAN and Wi-Fi at the same time). Windows
    /// includes every bound address in a name query response by default.
    /// </summary>
    SameHost
}
