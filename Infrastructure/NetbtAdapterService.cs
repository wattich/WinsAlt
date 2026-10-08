using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;

namespace WinsAlt.Infrastructure;

/// <summary>One network adapter and its Windows "NetBIOS over TCP/IP" setting.</summary>
public sealed record NetbtAdapter(string Id, string Name, string Description, IReadOnlyList<string> Addresses,
    bool Enabled, string Setting);

/// <summary>
/// Reads and switches Windows' own NetBIOS over TCP/IP (NetBT) per adapter. NetBT is what holds
/// UDP 137 on a stock Windows machine, so it has to be off on the adapter the name server
/// listens on.
///
///  - Read: the per-interface <c>NetbiosOptions</c> registry value (0 = default / follow DHCP,
///    1 = enabled, 2 = disabled).
///  - Write: <c>Win32_NetworkAdapterConfiguration.SetTcpipNetbios</c> - the same call the adapter
///    properties dialog makes, which applies the change to the running stack. It is invoked
///    through powershell.exe because the managed WMI library (System.Management) is built on
///    COM interop + reflection and cannot be used from a Native AOT binary.
/// </summary>
public sealed class NetbtAdapterService
{
    private const string InterfacesKey = @"SYSTEM\CurrentControlSet\Services\NetBT\Parameters\Interfaces";
    private readonly ILogger<NetbtAdapterService> _logger;

    public NetbtAdapterService(ILogger<NetbtAdapterService> logger) => _logger = logger;

    public bool Supported => OperatingSystem.IsWindows();

    public IReadOnlyList<NetbtAdapter> List()
    {
        var adapters = new List<NetbtAdapter>();
        if (!OperatingSystem.IsWindows()) return adapters;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            // A disconnected adapter holds no port, so it cannot be in the listener's way.
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || nic.OperationalStatus != OperationalStatus.Up) continue;

            var addresses = nic.GetIPProperties().UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .ToList();
            if (addresses.Count == 0) continue;

            // Adapters NetBT is not bound to have no key here - nothing to switch.
            using var key = Registry.LocalMachine.OpenSubKey($@"{InterfacesKey}\Tcpip_{nic.Id}");
            if (key is null) continue;

            int option = key.GetValue("NetbiosOptions") is int value ? value : 0;
            adapters.Add(new NetbtAdapter(nic.Id, nic.Name, nic.Description, addresses,
                Enabled: option != 2,
                Setting: option switch { 1 => "Enabled", 2 => "Disabled", _ => "Default" }));
        }

        adapters.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return adapters;
    }

    /// <summary>Switches NetBT on one adapter. Returns an error message, or null on success.</summary>
    public async Task<string?> SetAsync(string adapterId, bool enabled, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return "NetBIOS over TCP/IP only exists on Windows.";

        // The id is interpolated into a command line: accept nothing but an adapter GUID we listed.
        if (!Guid.TryParse(adapterId, out var guid) || List().All(a => !string.Equals(a.Id, adapterId, StringComparison.OrdinalIgnoreCase)))
            return "Unknown network adapter.";

        // 0 = default (on, unless DHCP says otherwise) - what the adapter had before; 2 = off.
        int option = enabled ? 0 : 2;
        // Get-WmiObject rather than the CIM cmdlets: it exists in every Windows PowerShell from
        // 2.0 on, and 2.0 is all that Windows 7 / Server 2008 R2 ship with.
        string script =
            $"$c = Get-WmiObject Win32_NetworkAdapterConfiguration -Filter \\\"SettingID='{guid:B}'\\\"; " +
            "if (-not $c) { exit 90 }; " +
            $"$r = $c.SetTcpipNetbios({option}); " +
            "exit [int]$r.ReturnValue";

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            });
            if (process is null) return "Could not start powershell.exe.";

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);

            // WMI return values: 0 = done, 1 = done but a reboot is required.
            string? error = process.ExitCode switch
            {
                0 => null,
                1 => null,
                90 => "Windows could not find that adapter through WMI.",
                91 => "Access denied - the service must run as LocalSystem or an administrator.",
                _ => $"Windows refused the change (code {process.ExitCode}). The service needs administrator rights to change adapter settings."
            };

            if (error is null)
                _logger.LogWarning("NetBIOS over TCP/IP {State} on adapter {Adapter} from the dashboard{Reboot}",
                    enabled ? "enabled" : "disabled", guid.ToString("B"), process.ExitCode == 1 ? " (Windows asks for a restart)" : "");
            return error;
        }
        catch (OperationCanceledException)
        {
            return "Timed out waiting for Windows to apply the change.";
        }
        catch (Exception ex)
        {
            return "Could not run the adapter change: " + ex.Message;
        }
    }
}
