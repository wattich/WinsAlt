# Roadmap

แผนงานขนาดใหญ่ของ WinsAlt งานที่เสร็จแล้วอยู่ใน [CHANGELOG.md](../CHANGELOG.md)

| # | งาน | สถานะ |
|---|---|---|
| 1 | [เวอร์ชันสำหรับ Linux](#1-เวอร์ชันสำหรับ-linux) | **ขั้นที่ 1 เสร็จ (1.5.0)** - รันได้บน Debian 13 (WSL) และ Ubuntu 24.04 (VM จริง, 1.5.4); ขั้นที่ 2 ยังไม่เริ่ม |
| 2 | Replication กับ Microsoft WINS (MS-WINSRA บน TCP 42) | ยังไม่เริ่ม |

---

## 1. เวอร์ชันสำหรับ Linux

### เป้าหมาย

ติดตั้ง WinsAlt บน Linux เป็น service ของ systemd ให้ทำงานเหมือนบน Windows: name server, replication,
query forwarding, DNS fallback, dashboard, หน้า Settings, ปุ่ม Restart service และตาราง firewall  - 
และ replicate กับ WinsAlt บน Windows ได้ (protocol เดียวกัน)

### Distro ที่รองรับ

ตกลงกัน 2026-10-06: **เฉพาะรุ่นที่อยู่ในรายการของ .NET 10 และเป็นรุ่นใหม่ ไม่ย้อนรุ่นเก่า**

| Distro | รุ่น | สถานะ |
|---|---|---|
| Debian | 13 (trixie) | **ทดสอบแล้วใน WSL** (1.5.0) |
| Ubuntu | 24.04 LTS | **ทดสอบแล้วบน VM จริง** (1.5.4, ufw) |
| Ubuntu | 26.04 LTS | **ทดสอบแล้วบน VM จริง** (1.6.0, upgrade จาก 24.04; replication กับ SERVER-A) |
| CentOS Stream | 10 | **ทดสอบแล้วบน VM จริง** (1.7.4, firewalld, SELinux Enforcing - ไม่มี AVC) |

อ้างอิง [.NET 10 supported OS](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md) (ตรวจ 2026-10-06):
glibc ขั้นต่ำ 2.27 สำหรับ x64 - binary เดียวใช้ได้กับทุกรุ่นในตาราง ต่างกันแค่ firewall และ SELinux

ไม่รองรับ (ตัดสินใจแล้ว): Debian 12, Ubuntu 22.04, Rocky / Alma, RHEL 8, CentOS 7

### ที่เก็บไฟล์และสิทธิ์ (ที่ทำแล้วใน 1.5.0)

- โปรแกรม `/opt/winsalt/winsalt`, ค่าตั้งต้น `/opt/winsalt/appsettings.json` (ไม่ถูกทับตอน upgrade)
- ข้อมูล `/var/lib/winsalt` (`Wins__DataDirectory` ใน unit): `wins-db.json`, `static-mappings.json`, `partners.json`,
  `replication.json`, `auth.json`, `settings.json` (ค่าที่บันทึกจากหน้า Settings)
- unit `/etc/systemd/system/winsalt.service`: `Type=notify`, `Restart=on-failure`, exit code 75 = restart ที่สั่งจาก dashboard
  (`SuccessExitStatus=75` + `RestartForceExitStatus=75` จึงไม่ถูกบันทึกเป็น failure)
- **รันเป็น root** - ตัดสินใจเพราะผู้ใช้ต้องการให้กดปุ่ม firewall จากหน้าเว็บได้ (firewall-cmd / ufw ต้องการ root)
  และ UDP 137 เป็น port ต่ำกว่า 1024; จำกัดด้วย sandbox ของ systemd: `ProtectSystem=full` (+ `ReadWritePaths=-/etc/ufw`),
  `ProtectHome`, `PrivateTmp`, `NoNewPrivileges`, `RestrictAddressFamilies`, `Protect*` อื่น ๆ
- `Conflicts=nmbd.service` - Samba `nmbd` ถือ UDP 137; install.sh หยุดติดตั้งถ้า nmbd ทำงานอยู่

### สิ่งที่เปลี่ยนในโค้ด (1.5.0)

| ไฟล์ | Windows | Linux |
|---|---|---|
| `WinsAlt.csproj` | `win-x64` (ค่าเริ่มต้น) | `-r linux-x64` จาก build-linux.sh; package `Hosting.Systemd` |
| `Program.cs` | `UseWindowsService` | `UseSystemd` (journald, readiness); `return Environment.ExitCode` |
| `ServiceControl.cs` | helper process + Service Control Manager | หยุด process ปกติด้วย exit code 75 → systemd start ใหม่ |
| `Infrastructure/FirewallService.cs` | `netsh advfirewall` | `firewall-cmd` (runtime + permanent, default zone) หรือ `ufw allow` / `ufw delete allow`; ไม่มีทั้งสอง = แสดงว่าไม่มี firewall |
| `Network/SelfRegistrationService.cs` | `NetGetJoinInformation` | `workgroup =` ใน `/etc/samba/smb.conf` ถ้ามี, ไม่มี = ใช้ `Wins:SelfGroup` |
| `Network/NbnsServer.cs`, `ReplicationWorkers.cs` | `ExclusiveAddressUse` | ไม่ตั้ง (Linux แยก port อยู่แล้วถ้าไม่ใช้ `SO_REUSEADDR`); ข้อความ error บอกเรื่อง nmbd / root |
| `Infrastructure/NetbtAdapterService.cs` | ตาราง NetBIOS ของ Windows | ไม่แสดง (ไม่มีบน Linux) |
| `wwwroot/index.html` | - | ข้อความ firewall / restart ตาม OS (`firewall.firewall`, `settings.serviceManager`) |
| `tools/*.ps1` | - | รันด้วย `pwsh` บน Linux ได้ (`$IsLinux`: path ของ binary, `Start-Process` แบบไม่มี window) |

### Build และทดสอบบนเครื่อง dev (WSL Debian 13)

- `build-linux.ps1` (Windows) → `build-linux.sh` ใน WSL → `publish/linux-x64/` และ `Output/WinsAlt_v<ver>_linux-x64.tar.gz`
- เครื่องมือใน WSL (ติดตั้ง 2026-10-06): .NET 10 SDK ที่ `/opt/dotnet10`, `clang`, `rsync`, PowerShell 7 ที่ `/opt/microsoft/powershell/7`
 - ไม่มีตัวไหนอยู่ใน PATH; ดิสก์ WSL ใช้เพิ่มราว 1 GB (8.8 → 9.7 GB)
- **WSL นี้เป็นของโปรเจกต์ `<another project> IP-PBX` ด้วย** (FreeSWITCH, controller .NET 8 = `/usr/bin/dotnet`, nginx :80,
  PostgreSQL :5442, Redis :6379, fail2ban) กติกาการใช้ร่วม:
  - ไม่แตะ `/usr/bin/dotnet`, PATH, nginx, fail2ban; build ใน `/root/winsalt` (ไม่ build ผ่าน `/mnt/f`)
  - instance ทดสอบใช้ UDP 137 / 11137, TCP 8137 / 8138 / 18137 / 18138 - ไม่ชนกับ SIP
  - ทดสอบ systemd ด้วย `systemctl start` เท่านั้น **ไม่ enable** และ uninstall ทุกครั้งหลังทดสอบ - ไม่มีอะไร start ตอนบูต WSL
  - ปิด process ทดสอบด้วย PID (หาจาก port) ห้าม `pkill -f`; ไม่ติดตั้ง Samba / firewalld / ufw ใน WSL นี้
- ผลทดสอบ 1.5.0 ใน WSL: `nbns-smoke` (port ทดสอบ และ port 137 จริง), `replication-smoke`, `security-smoke` ผ่านครบ;
  ติดตั้งเป็น service จริงด้วย install.sh → ปุ่ม Restart service ผ่าน API (PID เปลี่ยน, ค่าที่รอ restart มีผล) → uninstall --purge;
  เปิด dashboard ของ instance ใน WSL จาก Edge บน Windows

- 1.6.0: `install.sh` ตรวจ distro (+ `--force`), ติดตั้ง OpenSSL 3 / iproute2 / curl ที่ขาดด้วย apt / dnf, ตรวจ port (137/udp, 8138/tcp;
  8137 ไม่ว่างใช้ port สำรอง), เปิด port ใน ufw / firewalld ที่เปิดอยู่แล้ว, ตรวจว่า listener ขึ้น; dashboard เปิด LAN + sign in + admin / admin

### ยังไม่ได้ทดสอบ / ยังไม่ได้ทำ

- [x] ปุ่ม firewall กับ **ufw** กดจริงจากหน้าเว็บบน Ubuntu 24.04 (1.5.4) - Allow / Remove ใช้ได้ทั้ง v4/v6
- [x] ปุ่ม firewall กับ **firewalld** กดจริงบน CentOS Stream 10 (1.7.4) - Allow / Remove ใช้ได้ทั้ง runtime และ permanent
- [x] Replication Windows ↔ Linux กับเครื่องจริง (SERVER-A ↔ VM 26.04, 1.6.0); การลบ IP ค้างอัตโนมัติ (1.7.0-1.7.2) ถูกถอนใน 1.7.3
- [x] เครื่อง Linux จริงบน LAN: `nbtstat -A` และ `nbns-smoke` จาก Windows ข้ามเครือข่ายมาที่ UDP 137 ของ Ubuntu ผ่าน (1.5.4)
- [x] CentOS Stream 10 + SELinux Enforcing: ไม่มี AVC denial (service เป็น `unconfined_service_t`) - ไม่ต้องทำ policy
- [x] Ubuntu 24.04 (1.5.4), Ubuntu 26.04 (1.6.0)
- [ ] ข้อความในหน้า Settings บางหัวข้อยังพูดถึงแต่ Windows (เช่น Listen address: "ปิด NetBIOS over TCP/IP ของ Windows")

### ขั้นที่ 2 - แพ็กเกจและของเสริม

1. ~~แพ็กเกจ `.deb` (Debian / Ubuntu) และ `.rpm` (CentOS Stream)~~ ทำแล้วใน 1.8.1 (คู่กับ tarball, ใช้ install.sh ตัวเดียวกัน; .rpm ทดสอบบน CentOS แล้ว, .deb ยังไม่ได้ติดตั้งจริง)
2. ตรวจ Samba `nmbd` ในหน้า Overview แทนตาราง NetBIOS ของ Windows
3. ~~SELinux policy~~ ไม่จำเป็น (ทดสอบ 1.7.4)
4. `linux-arm64` (ถ้าต้องการ)

### ต้องได้คำตอบ

- [x] distro: Debian 13, Ubuntu 26.04 / 24.04, CentOS Stream 10 (ตอบ 2026-10-06)
- [x] ใช้ WSL Debian 13 บนเครื่อง dev ได้ ตามกติกาด้านบน (ตอบ 2026-10-06)
- [x] ตาราง firewall บน Linux กดจากหน้าเว็บได้ (ตอบ 2026-10-06) → service รันเป็น root
- [x] VM Ubuntu 24.04 มีแล้ว (`wins-linux` 192.0.2.13, 2026-10-07)
- [x] VM CentOS Stream 10 มีแล้ว (192.0.2.14, 2026-10-07)
