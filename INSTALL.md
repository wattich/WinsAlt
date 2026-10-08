# Installing WinsAlt

[ภาษาไทย](INSTALL.th.md)

A short guide from download to the first client resolving names. For everything else (settings, replication,
security) see the [README](README.md).

## 1. Download

Take the files of the latest version from the [Releases page](../../releases/latest):

| System | File |
|---|---|
| Windows 10 / 11, Windows Server 2012 or later (x64) | `WinsAlt_Setup_v<version>.exe` |
| Debian 13, Ubuntu 24.04 / 26.04 (x86_64) | `winsalt_<version>-1_amd64.deb` |
| CentOS Stream 10 and other RHEL 10 family (x86_64) | `winsalt-<version>-1.x86_64.rpm` |
| Any of the Linux systems above, without a package manager | `WinsAlt_v<version>_linux-x64.tar.gz` |

`SHA256SUMS.txt` lists the checksum of every file. To check a download:

```bash
sha256sum -c SHA256SUMS.txt --ignore-missing          # Linux
```
```powershell
Get-FileHash .\WinsAlt_Setup_v<version>.exe            # Windows: compare with the line in SHA256SUMS.txt
```

Before you start, plan for:

- **UDP 137** on the server must be free. On Windows, the system's own "NetBIOS over TCP/IP" holds it (step 3
  shows how to release it). On Linux, Samba's `nmbd` must be stopped.
- **TCP 8138** is used for replication between WinsAlt servers, **TCP 8137** for the dashboard (another free
  port is chosen if 8137 is taken).
- Give a Linux server a real host name first (`hostnamectl set-hostname <name>`).

## 2. Install

### Windows

1. Run `WinsAlt_Setup_v<version>.exe` as an administrator. The installer is not code-signed yet, so
   SmartScreen may say "Windows protected your PC": choose **More info**, then **Run anyway**.
2. Accept the MIT license, then the scope and limitations of use.
3. Keep the firewall task ticked unless you manage Windows Firewall yourself (it opens UDP 137, TCP 8138
   and the dashboard port for WinsAlt only).
4. At the end the installer shows the dashboard address and whether the name server is listening on
   UDP 137. A desktop shortcut to the dashboard is created if that task was ticked.

The program goes to `C:\Program Files\WinsAlt` and runs as the `WinsAlt` service (automatic start).

### Debian / Ubuntu (.deb)

```bash
sudo apt install ./winsalt_<version>-1_amd64.deb
```

### CentOS Stream / RHEL family (.rpm)

```bash
sudo dnf install ./winsalt-<version>-1.x86_64.rpm
```

Both packages install the dependencies (OpenSSL 3, iproute, curl), check the ports, start the `winsalt`
service and open the ports in ufw or firewalld when one of them is active. The last lines printed give the
dashboard address. SELinux (Enforcing) needs no extra policy.

### Linux without a package (.tar.gz)

```bash
tar -xzf WinsAlt_v<version>_linux-x64.tar.gz
cd winsalt-<version>
sudo ./install.sh          # --no-firewall: leave ufw / firewalld alone; --no-start: install only
```

The result is the same as with a package: program in `/opt/winsalt`, data in `/var/lib/winsalt`,
service `winsalt`, logs with `journalctl -u winsalt -f`.

## 3. First sign-in

1. Open the dashboard: `http://<server address>:8137` (or the port the installer reported).
2. Sign in with **admin / admin**. The page asks for a new password straight away: set it before anything else.
3. **Windows only:** on the **Overview** page, the table "Windows NetBIOS over TCP/IP on this server" lists
   the network adapters. Press **Turn off** on the adapter the clients use. The name server starts listening
   on UDP 137 a few seconds later and WinsAlt publishes the server's own name itself.

The language of the dashboard follows the browser (English or Thai); the selector at the bottom of the page
changes it.

## 4. Point the clients at it

Set the WINS server of the clients to this server, usually through DHCP:

- option **044** (WINS/NBNS servers) = the address of this server (a second WinsAlt server as the second entry)
- option **046** (NetBIOS node type) = `0x8` (H-node)

Clients pick up the change on their next DHCP renewal (`ipconfig /renew` to do it at once). On a client,
`nbtstat -n` shows its names as "Registered", and the dashboard's **Registered names** page lists them.

Firewalls and routers between sites must allow UDP 137 to the server in both directions.

## 5. Upgrade

Install the new version over the old one, the same way as the first time:

- Windows: run the new `WinsAlt_Setup_v<version>.exe`
- `.deb`: `sudo apt install ./winsalt_<new version>-1_amd64.deb`
- `.rpm`: `sudo dnf install ./winsalt-<new version>-1.x86_64.rpm`
- `.tar.gz`: unpack the new one and run `sudo ./install.sh`

Settings, names, static mappings, partners, replication and the password are kept. A server installed from
the `.tar.gz` can be upgraded with the `.deb` or `.rpm`; both use the same folders.

## 6. Uninstall

| Installed with | Remove the program, keep the data | Remove everything |
|---|---|---|
| Windows installer | Apps and features, WinsAlt, Uninstall | then delete `C:\Program Files\WinsAlt` |
| `.deb` | `sudo apt remove winsalt` | `sudo apt purge winsalt` |
| `.rpm` | `sudo dnf remove winsalt` | then `sudo rm -rf /var/lib/winsalt` |
| `.tar.gz` | `sudo ./uninstall.sh` | `sudo ./uninstall.sh --purge` |

On Windows, turn "NetBIOS over TCP/IP" back on for the adapter afterwards if the server should go back to
using it.

## 7. If something does not work

| What you see | What to check |
|---|---|
| Name server "not listening" | Windows: NetBIOS over TCP/IP is still on for that adapter (step 3). Linux: `nmbd` or another program holds UDP 137 (`ss -ulpn 'sport = :137'`). |
| Dashboard does not open from another computer | The firewall on the server (dashboard port), or "Open the dashboard to other computers" is off in Settings. |
| Clients do not register | DHCP option 044, a firewall between client and server (UDP 137), or the registration policy in Settings. |
| Cannot sign in at all | Delete `auth.json` in the data folder and restart the service: the sign-in is admin / admin again. |

Logs: Windows Event Viewer (Windows Logs, Application); Linux `journalctl -u winsalt`.
