using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WinsAlt;

/// <summary>
/// Small one-shot commands the installer runs instead of generating helper scripts. Each one
/// answers through its process exit code and never starts the server.
///
///   WinsAlt.exe --pick-port 8137       exit code = a free dashboard port (the preferred one if free)
///   WinsAlt.exe --listener-state 8137  exit code = 0 listening, 1 not listening, 2 no answer
///
/// These used to be PowerShell snippets written to %TEMP% and run with -ExecutionPolicy Bypass  - 
/// a pattern antivirus behaviour monitoring (rightly) distrusts in an installer.
/// </summary>
internal static class InstallerCommands
{
    private static readonly int[] FallbackPorts = [8137, 8138, 8139, 18137, 9137];

    /// <summary>Returns the exit code when <paramref name="args"/> is an installer command, otherwise null.</summary>
    public static int? TryRun(string[] args)
    {
        if (args.Length != 2 || !int.TryParse(args[1], out int port) || port is < 1 or > 65535) return null;

        return args[0] switch
        {
            "--pick-port" => PickPort(port),
            "--listener-state" => ListenerState(port),
            _ => null
        };
    }

    private static int PickPort(int preferred)
    {
        if (IsFree(preferred)) return preferred;
        foreach (int candidate in FallbackPorts)
            if (IsFree(candidate)) return candidate;
        return preferred;
    }

    // "Free" means bindable on BOTH 0.0.0.0 and 127.0.0.1: Windows lets a 0.0.0.0 bind succeed
    // while another process holds 127.0.0.1:<port>, and localhost traffic then goes to that
    // more specific socket instead of ours.
    private static bool IsFree(int port) => CanBind(IPAddress.Any, port) && CanBind(IPAddress.Loopback, port);

    private static bool CanBind(IPAddress address, int port)
    {
        try
        {
            var listener = new TcpListener(address, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// Polls the freshly started service for up to ~10 seconds. Reads the public /api/info: /api/status needs a
    /// sign-in by default since 1.6.0 (the server's own console included), so it would only ever answer 401 here.
    /// </summary>
    private static int ListenerState(int dashboardPort)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            Thread.Sleep(1000);
            string body = Get(dashboardPort, "/api/info");
            if (body.Contains("\"listener\":\"Active\"", StringComparison.Ordinal)) return 0;
            if (body.Contains("\"listener\":\"Error\"", StringComparison.Ordinal)) return 1;
        }
        return 2;
    }

    private static string Get(int port, string path)
    {
        try
        {
            using var client = new TcpClient();
            client.SendTimeout = client.ReceiveTimeout = 3000;
            client.Connect(IPAddress.Loopback, port);

            using var stream = client.GetStream();
            stream.Write(Encoding.ASCII.GetBytes($"GET {path} HTTP/1.0\r\nHost: localhost\r\nConnection: close\r\n\r\n"));

            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception)
        {
            return "";
        }
    }
}
