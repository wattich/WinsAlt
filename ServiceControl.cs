using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace WinsAlt;

/// <summary>
/// Restarting the service from its own dashboard.
///
/// Linux (systemd): the process stops normally with exit code <see cref="RestartExitCode"/>, and the
/// unit's <c>Restart=on-failure</c> starts it again - systemd does the restarting.
///
/// Windows:
/// A service cannot restart itself: once it has stopped there is nobody left to start it. So the
/// running service starts a second copy of this executable  - 
///
///   WinsAlt.exe --restart-service
///
/// - which outlives it, asks the Service Control Manager to stop the service (an ordinary stop:
/// the name database is saved exactly as on any other stop), waits until it is gone, and starts
/// it again. The SCM is called directly (advapi32); no cmd.exe, net.exe or PowerShell is involved.
/// What the helper did is written to service-restart.log beside the executable, the only place
/// left to look when a restart did not come back.
/// </summary>
internal static class ServiceControl
{
    public const string ServiceName = "WinsAlt";
    private const string RestartCommand = "--restart-service";
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Exit code that tells systemd to start the service again (any failure code would; 75 = EX_TEMPFAIL).</summary>
    public const int RestartExitCode = 75;

    /// <summary>Started by the operating system's service manager (Windows SCM or systemd), not from a console.</summary>
    public static bool RunningAsService => OperatingSystem.IsWindows() ? WindowsServiceHelpers.IsWindowsService() : SystemdHelpers.IsSystemdService();

    /// <summary>What runs this process, for the dashboard: "Windows service", "systemd" or "" (a console).</summary>
    public static string Manager => !RunningAsService ? "" : OperatingSystem.IsWindows() ? "Windows service" : "systemd";

    /// <summary>Only a process a service manager started can be restarted through it.</summary>
    public static bool CanRestart => RunningAsService;

    /// <summary>Returns the exit code when <paramref name="args"/> is the restart command, otherwise null.</summary>
    public static int? TryRun(string[] args) =>
        args.Length == 1 && args[0] == RestartCommand ? RestartService() : null;

    /// <summary>Starts the helper that restarts the service. Returns an error message, or null once it is running.</summary>
    public static string? BeginRestart(IHostApplicationLifetime lifetime)
    {
        if (!CanRestart) return "WinsAlt is not running as a service here, so there is nothing to restart - stop and start the program yourself.";

        if (!OperatingSystem.IsWindows())
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(700); // let the "restart requested" answer leave first
                Environment.ExitCode = RestartExitCode;
                lifetime.StopApplication();
            });
            return null;
        }

        if (Environment.ProcessPath is not { } exe) return "Could not determine this program's path.";

        try
        {
            using var helper = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = RestartCommand,
                WorkingDirectory = AppContext.BaseDirectory,
                CreateNoWindow = true,
                UseShellExecute = false
            });
            return helper is null ? "Could not start the restart helper." : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return "Could not start the restart helper: " + ex.Message;
        }
    }

    // ---------- the helper process ----------

    private static int RestartService()
    {
        var log = new List<string>();
        void Note(string message) => log.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}");

        int exit = 1;
        try
        {
            // Let the dashboard's "restart requested" answer leave before the service goes down.
            Thread.Sleep(700);
            exit = Restart(Note);
        }
        catch (Exception ex)
        {
            Note("Failed: " + ex.Message);
        }

        try { File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "service-restart.log"), log); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* nowhere else to report it */ }
        return exit;
    }

    private static int Restart(Action<string> note)
    {
        IntPtr manager = OpenSCManagerW(IntPtr.Zero, IntPtr.Zero, ScManagerConnect);
        if (manager == IntPtr.Zero) { note("Cannot reach the Service Control Manager: " + LastError()); return 2; }

        IntPtr name = Marshal.StringToHGlobalUni(ServiceName);
        IntPtr service = IntPtr.Zero;
        try
        {
            service = OpenServiceW(manager, name, ServiceStop | ServiceStart | ServiceQueryStatus);
            if (service == IntPtr.Zero) { note($"Cannot open the service {ServiceName}: " + LastError()); return 3; }

            if (State(service) != ServiceStopped)
            {
                // "Not active" just means it stopped between the two calls.
                if (!ControlService(service, ServiceControlStop, out _) && Marshal.GetLastWin32Error() != ErrorServiceNotActive)
                {
                    note("The service refused to stop: " + LastError());
                    return 4;
                }
                note("Stop requested.");

                if (!WaitFor(service, ServiceStopped, StopTimeout))
                {
                    note($"The service did not stop within {StopTimeout.TotalSeconds:0} seconds - not started again.");
                    return 5;
                }
            }
            note("Stopped.");

            // Windows can take a moment to let go of the ports; a start that fails is tried again.
            for (int attempt = 1; ; attempt++)
            {
                if (StartServiceW(service, 0, IntPtr.Zero) || Marshal.GetLastWin32Error() == ErrorServiceAlreadyRunning)
                {
                    if (WaitFor(service, ServiceRunning, StartTimeout)) { note("Started."); return 0; }
                    note("Start requested, but the service is not running: state " + State(service));
                }
                else note($"Start attempt {attempt} failed: " + LastError());

                if (attempt == 3) return 6;
                Thread.Sleep(2000);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(name);
            if (service != IntPtr.Zero) CloseServiceHandle(service);
            CloseServiceHandle(manager);
        }
    }

    private static uint State(IntPtr service) => QueryServiceStatus(service, out var status) ? status.CurrentState : 0;

    private static bool WaitFor(IntPtr service, uint wanted, TimeSpan timeout)
    {
        var until = Stopwatch.StartNew();
        while (until.Elapsed < timeout)
        {
            if (State(service) == wanted) return true;
            Thread.Sleep(250);
        }
        return State(service) == wanted;
    }

    private static string LastError() => new Win32Exception(Marshal.GetLastWin32Error()).Message;

    // ---------- advapi32 (blittable signatures only, so no marshalling code is generated) ----------

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004, ServiceStart = 0x0010, ServiceStop = 0x0020;
    private const uint ServiceControlStop = 1;
    private const uint ServiceStopped = 1, ServiceRunning = 4;
    private const int ErrorServiceAlreadyRunning = 1056, ErrorServiceNotActive = 1062;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(IntPtr machineName, IntPtr databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr manager, IntPtr serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceW(IntPtr service, uint argumentCount, IntPtr arguments);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
