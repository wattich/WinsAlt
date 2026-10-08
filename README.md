# WinsAlt - NetBIOS Name Server (WINS replacement)

English | [ภาษาไทย](README.th.md)

WinsAlt answers NetBIOS name queries for networks that still depend on WINS. It speaks the NetBIOS
Name Service protocol (NBNS, RFC 1001/1002) on UDP 137, so legacy Windows machines, printers and
other devices keep working unchanged, but it ships as one self-contained Native AOT executable
(about 12 MB) with a web dashboard built in. It runs on Windows (as a service) and on Linux (systemd).

- Name registration, refresh, release and query; unique, group, internet group `<1C>` and multihomed names
- In-memory name database with automatic expiry, saved to `wins-db.json` and restored after a restart
- Static mappings (LMHOSTS-style) from the dashboard or `static-mappings.json`
- Replication between WinsAlt servers: each server copies the names the others own and answers for them itself
  (TCP 8138, authenticated with a shared key)
- Query forwarding of unknown names to other WINS servers, Microsoft WINS included (one way, not replication)
- DNS fallback for names found nowhere else
- Web dashboard in English and Thai, with language files anyone can add to
- Security controls for an unauthenticated protocol: registration policy, blocked names, rate limits, quotas

More: [architecture](docs/ARCHITECTURE.md), [changelog](CHANGELOG.md), [roadmap](docs/ROADMAP.md),
[contributing and translations](CONTRIBUTING.md), [security policy](SECURITY.md).
Parts of the design notes, changelog and roadmap are written in Thai.

## Install

Downloads are on the [Releases page](../../releases/latest): the Windows installer, a `.deb`, a `.rpm` and a
`.tar.gz` for Linux, and `SHA256SUMS.txt`. Step by step, from download to the first client: [INSTALL.md](INSTALL.md).

### Windows

Download `WinsAlt_Setup_v<version>.exe` and run it. The installer:

- shows the MIT license and the [scope and limitations of use](installer/usage-terms.en.txt), both to be accepted
- installs to `Program Files\WinsAlt` and creates the `WinsAlt` service (automatic start, restart on failure)
- picks a free dashboard port (8137 if free), and optionally opens UDP 137, TCP 8138 and the dashboard port in
  Windows Firewall
- tells you at the end whether the name server is really listening on UDP 137

An upgrade stops the service, replaces the program and starts it again; settings and data are kept.

**Before it can serve clients**, Windows' own "NetBIOS over TCP/IP" must let go of UDP 137 on the adapter
clients use. The dashboard's Overview page has a table of adapters with a **Turn off** button for this
(or: adapter properties, IPv4, Advanced, WINS, Disable NetBIOS over TCP/IP). WinsAlt then publishes this
machine's own name itself. See "Switching off NetBIOS on the server" below for what else changes.

The installer is not code-signed yet, so Windows may warn about an unknown publisher.

### Linux (Debian 13, Ubuntu 24.04 / 26.04, CentOS Stream 10, x86_64)

```bash
sudo apt install ./winsalt_<version>-1_amd64.deb      # Debian / Ubuntu
sudo dnf install ./winsalt-<version>-1.x86_64.rpm     # CentOS Stream / RHEL family
```

or, without a package manager:

```bash
tar -xzf WinsAlt_v<version>_linux-x64.tar.gz
cd winsalt-<version>
sudo ./install.sh              # install or upgrade, then enable and start the service
                               #   --no-start     install only
                               #   --no-firewall  do not open ports in ufw / firewalld
                               #   --force        install on a distribution that is not on the list (untested)
```

The packages carry the same `install.sh` and run it after unpacking, so all three end in the same place and
one can upgrade another. `install.sh` checks the distribution, installs what is missing (OpenSSL 3, iproute2, curl), checks that
UDP 137 and TCP 8138 are free (Samba's `nmbd` must be stopped: `systemctl disable --now nmbd`), picks
another dashboard port if 8137 is taken, and opens the ports in ufw or firewalld when one is already active.

- Program in `/opt/winsalt`, data in `/var/lib/winsalt`, service `winsalt`, logs: `journalctl -u winsalt -f`
- Give the machine a real host name first (`hostnamectl set-hostname <name>`); a machine still called
  `localhost` does not publish its own name
- SELinux (CentOS Stream): works without extra policy
- Remove: `sudo apt remove winsalt` / `sudo dnf remove winsalt` / `sudo ./uninstall.sh` keep the data;
  `sudo apt purge winsalt` or `sudo ./uninstall.sh --purge` remove it too

### Point the clients at it

Set the WINS server of the clients to this machine, usually through DHCP (option 044 = this server,
option 046 = `0x8`, H-node). Firewalls and routers between sites must allow UDP 137 (and TCP 8138 between
replicating servers) in both directions.

## Dashboard

`http://<server>:8137`

- **Sign in first.** A new installation signs in with **admin / admin** and immediately asks for a new password
  (at least 8 characters); a red notice stays until it is changed. Visitors who are not signed in see nothing
  ("Sign-in required to view" is on by default, for every computer, the server itself included).
- **Language**: English or Thai, chosen from the browser's language the first time; switch at the bottom right.
- **Settings** tab: every setting has a **?** button that explains it; values are checked before they are saved,
  and settings that need a restart say so and offer a **Restart service** button.
- **Firewall** table on the Settings page: allow or remove the inbound rules (Windows Firewall, firewalld or ufw).
- Forgot the password: stop the service, delete `auth.json` from the data folder, start it again, sign in with
  admin / admin.
- Scripts: set an *API token for scripts* in Settings and send it in the `X-Admin-Token` header. Every changing
  request must also carry `X-Requested-With: WinsAlt`.

The dashboard is plain HTTP. Keep it on a management network or behind a firewall.

### Adding a language

Copy `wwwroot/lang/en.json` to `<code>.json` (for example `de.json`), translate the values and keep the keys,
then put the file in the `lang` folder of the data folder (`C:\Program Files\WinsAlt\lang` on Windows,
`/var/lib/winsalt/lang` on Linux). Reload the page: the language appears in the selector. Missing keys fall
back to English. A file with a built-in code (`th.json`) replaces the built-in translation. Details and how to
contribute a language to the project: [CONTRIBUTING.md](CONTRIBUTING.md).

## Configuration

Almost everything is set from the dashboard's Settings tab, which writes `settings.json` in the data folder.
That file sits above `appsettings.json` (which is never modified) and below environment variables
(`Section__Key`, for example `Wins__Port`). Still set only in `appsettings.json`: the dashboard address and port
(`Kestrel:Endpoints:Management:Url`), `Wins:DataDirectory`, `Wins:Workers` and logging.

| Key | Default | Meaning |
|---|---|---|
| `Wins:ListenAddress` / `Wins:Port` | `0.0.0.0` / `137` | Where the name server listens |
| `Wins:ConflictPolicy` | `Challenge` | `Challenge`, `Overwrite` or `Reject` when a unique name is claimed from another IP |
| `Wins:MinTtlSeconds` / `Wins:MaxTtlSeconds` | `21600` / `518400` | Registration lifetimes granted to clients |
| `Wins:ReplicationPort` | `8138` | TCP port other WinsAlt servers copy names from |
| `Wins:RegisterSelf`, `SelfName`, `SelfSuffixes`, `SelfGroup` | on | Publish this machine's own name and workgroup |
| `Wins:Dns:*` | enabled | DNS fallback: servers, suffix, timeout, cache |
| `Wins:Security:*` | permissive | See the next section |
| `Dashboard:RequireSignInToView` | `true` | Sign-in required to see anything |
| `Dashboard:AllowedHosts` | none | Extra host names the dashboard answers to (DNS rebinding protection) |

`static-mappings.json`:

```json
{
  "mappings": [
    { "name": "SERVER01", "addresses": ["192.168.1.10"], "comment": "example" },
    { "name": "CORP", "addresses": ["192.168.1.2", "192.168.1.3"], "suffixes": ["1C"], "group": true }
  ]
}
```

Without `suffixes` a mapping is registered as `00` (workstation) and `20` (file server).

## Security

NBNS has no authentication: anything that reaches UDP 137 can register a name, which is how poisoning tools
such as Responder work. WinsAlt's defaults stay compatible with WINS and only switch on what cannot hurt a
normal client; restricting who may register is up to you (Settings, Security):

| Key | Default | Meaning |
|---|---|---|
| `RegistrationMode` | `Dynamic` | `Dynamic`, `AllowedSubnets` (only listed networks) or `StaticOnly` |
| `AllowedSubnets` | none | CIDR list for `AllowedSubnets` |
| `RequireAddressMatchesSource` | `false` | A host may register only its own source address |
| `BlockedNames` | `WPAD, ISATAP` | Never registered dynamically, never forwarded |
| `MaxDynamicNames` / `MaxNamesPerAddress` | `100000` / `64` | Quotas (0 = no limit) |
| `RateLimitPerSecond` / `RateLimitBurst` | `100` / `300` | Per source address (0 = off) |
| `MaxConcurrentChallenges` | `64` | Owner checks running at once |
| `AllowMultihomedMerge` | `true` | Accept a second address the current owner confirms |

Names that must always resolve correctly belong in static mappings: they are answered before anything else and
cannot be overwritten by a client. Answers learned from partners and DNS are cached briefly and can be cleared
from the Partner servers tab.

## Replication and forwarding

**Between WinsAlt servers**: every server pulls, every 30 seconds, the complete set of names each peer owns,
and answers for them even when that site is unreachable. Use the same replication key everywhere, add every
server on every other server, and open TCP 8138 in both directions. A server never removes or shortens its own
registrations because of what a peer holds; stale addresses expire on their own.

**To other WINS servers** (Microsoft WINS included): names unknown here are asked of the partner servers on
UDP 137. This is one-way: names registered on WinsAlt are not copied to them.

Answer order: local static mapping, replicated static mapping, local registration, replicated registration,
partner servers, DNS.

## Switching off NetBIOS on the server (Windows)

WinsAlt needs UDP 137, so Windows' NetBIOS over TCP/IP must be off on that adapter, which also stops UDP 138 and
TCP 139 there. Clients are not affected, but the server itself changes:

| Windows used to | After switching it off |
|---|---|
| Publish this machine's name `<00>` `<20>` | WinsAlt publishes it (`Wins:RegisterSelf`) |
| Register workgroup / domain membership | WinsAlt does it (`Wins:SelfGroup`) |
| Answer `nbtstat -A` | WinsAlt answers |
| SMB over TCP 139 | Not available (SMB over 445 is unaffected) |
| Network Neighborhood / mailslots (UDP 138) | Not available |
| Resolve names through WINS / LMHOSTS on this machine | Not available; DNS and the hosts file only |

If any of these matters, run WinsAlt on a separate machine or VM.

## Build

```powershell
# compile check (AOT and trim analyzers are on; warnings are errors)
dotnet build -c Debug -p:PublishAot=false -p:SelfContained=false

# Windows release: Native AOT, publish\win-x64\WinsAlt.exe
powershell -ExecutionPolicy Bypass -File build-aot.ps1

# Windows installer (Inno Setup 6): Output\WinsAlt_Setup_v<version>.exe
powershell -ExecutionPolicy Bypass -File build-installer.ps1

# Linux, built in WSL (Debian 13 with clang and a .NET 10 SDK): Output\WinsAlt_v<version>_linux-x64.tar.gz,
# Output\winsalt_<version>-1_amd64.deb and Output\winsalt-<version>-1.x86_64.rpm (made with nfpm, fetched by the script)
powershell -ExecutionPolicy Bypass -File build-linux.ps1

# the files for a GitHub release, with SHA256SUMS.txt: Output\release-<version>\
powershell -ExecutionPolicy Bypass -File tools\release-files.ps1
```

The Native AOT build needs the Visual Studio Build Tools with "Desktop development with C++". The dashboard is an
embedded resource, so changes to `wwwroot/` need a rebuild. Bump the version in both `WinsAlt.csproj` and
`WinsAlt_Setup.iss`.

## Tests

The suites start their own instances on high test ports and send real NBNS packets:

```powershell
pwsh tools\security-smoke.ps1       # every security control, sign-in, settings, languages
pwsh tools\replication-smoke.ps1    # two instances replicating
# protocol suite against a running instance:
$env:Wins__Port='11137'; $env:Wins__ListenAddress='127.0.0.1'; $env:Wins__ChallengePort='13700'
$env:Kestrel__Endpoints__Management__Url='http://127.0.0.1:18137'; $env:Dashboard__AdminToken='smoke-test-admin-token'
.\publish\win-x64\WinsAlt.exe
pwsh tools\nbns-smoke.ps1 -Port 11137 -ChallengePort 13700 -Dashboard http://127.0.0.1:18137
```

The same scripts run on Linux with PowerShell 7.

## Limitations

- WINS replication with Microsoft WINS servers (MS-WINSRA, TCP 42) is not implemented; forwarding is one-way.
- No NetBIOS scope IDs, no name service over TCP 137; node status is answered only for the server itself.
- NBNS cannot be made secure: source addresses can be forged. The controls above reduce the risk.
- DNS fallback makes any single-label name DNS can resolve into a resolvable NetBIOS name
  (`Wins:Dns:Enabled=false` turns it off).

## License

[MIT](LICENSE). Third-party components and their licenses: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
WinsAlt is not affiliated with Microsoft. Windows and WINS are trademarks of Microsoft Corporation.
