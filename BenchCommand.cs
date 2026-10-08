using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using WinsAlt.Core.Domain;
using WinsAlt.Core.Protocol;

namespace WinsAlt;

/// <summary>
/// A small load generator for a running WinsAlt (or any NBNS server), using this build's own
/// packet codec:
///
///   WinsAlt.exe --bench &lt;server-ip&gt; &lt;port&gt; &lt;seconds&gt;
///
/// It registers one name, then several threads each send name queries for it and wait for the
/// answer, as fast as the server replies. Prints queries per second and the reply latency.
/// Pair it with the server's <c>metrics.allocatedBytes</c> / <c>gen0Collections</c> (GET
/// /api/status) before and after a run to see how much the packet path allocates.
/// Run it against a server started with <c>Wins:Security:RateLimitPerSecond = 0</c> - one source
/// address sending this fast is exactly what the rate limiter exists to stop.
/// </summary>
internal static class BenchCommand
{
    private const int Threads = 4;

    public static int? TryRun(string[] args)
    {
        if (args.Length != 4 || args[0] != "--bench") return null;
        if (!IPAddress.TryParse(args[1], out var server) || !int.TryParse(args[2], out int port) || !int.TryParse(args[3], out int seconds)
            || seconds is < 1 or > 600)
        {
            Console.Error.WriteLine("Usage: WinsAlt.exe --bench <server-ip> <port> <seconds>");
            return 2;
        }

        var endPoint = new IPEndPoint(server, port);
        NameKey.TryCreate("WINSALT-BENCH", 0x20, out var name);

        if (!Register(endPoint, name))
        {
            Console.Error.WriteLine($"No answer from {endPoint} - is the server running there?");
            return 1;
        }

        long answered = 0, lost = 0, latencyTicks = 0;
        var deadline = Stopwatch.GetTimestamp() + seconds * Stopwatch.Frequency;
        var workers = new Thread[Threads];
        for (int t = 0; t < Threads; t++)
        {
            ushort seed = (ushort)(t * 13_000);
            workers[t] = new Thread(() =>
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Connect(endPoint);
                socket.ReceiveTimeout = 500;

                Span<byte> query = stackalloc byte[NbnsHeader.Size + NbnsName.EncodedLength + 4];
                Span<byte> reply = stackalloc byte[NbnsPacketWriter.MaxPacket];
                ushort id = seed;
                long ok = 0, miss = 0, ticks = 0;

                while (Stopwatch.GetTimestamp() < deadline)
                {
                    int length = NbnsPackets.WriteNameQuery(query, ++id, name, recursionDesired: true);
                    long sent = Stopwatch.GetTimestamp();
                    try
                    {
                        socket.Send(query[..length]);
                        int received = socket.Receive(reply);
                        if (received >= NbnsHeader.Size && NbnsHeader.TryRead(reply, out var header) && header.TransactionId == id)
                        {
                            ok++;
                            ticks += Stopwatch.GetTimestamp() - sent;
                        }
                        else miss++;
                    }
                    catch (SocketException) { miss++; }
                }

                Interlocked.Add(ref answered, ok);
                Interlocked.Add(ref lost, miss);
                Interlocked.Add(ref latencyTicks, ticks);
            });
            workers[t].Start();
        }
        foreach (var worker in workers) worker.Join();

        double microseconds = answered == 0 ? 0 : latencyTicks * 1_000_000.0 / Stopwatch.Frequency / answered;
        Console.WriteLine($"queries answered : {answered:N0} in {seconds}s  ({answered / (double)seconds:N0}/s, {Threads} client threads)");
        Console.WriteLine($"mean reply time  : {microseconds:N0} µs");
        Console.WriteLine($"unanswered       : {lost:N0}");
        return 0;
    }

    private static bool Register(IPEndPoint endPoint, NameKey name)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Connect(endPoint);
        socket.ReceiveTimeout = 2000;

        // A registration request: question + one additional NB record (unique, H-node, 10.250.0.1).
        Span<byte> packet = stackalloc byte[128];
        var w = new NbnsPacketWriter(packet);
        w.WriteHeader(1, NbnsHeader.MakeFlags(false, NbnsOpcode.Registration, NbnsFlags.RecursionDesired, NbnsRcode.None), 1, 0, 0, 1);
        w.WriteQuestion(name, NbnsType.Nb);
        w.WriteRecordHeader(name, NbnsType.Nb, 300_000, 6);
        w.WriteUInt16(0x6000);
        w.WriteUInt32(0x0AFA0001);

        Span<byte> reply = stackalloc byte[NbnsPacketWriter.MaxPacket];
        try
        {
            socket.Send(packet[..w.Length]);
            return socket.Receive(reply) >= NbnsHeader.Size;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
