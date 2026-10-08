# WinsAlt - NetBIOS Name Server (WINS replacement)

[English](README.md) | ภาษาไทย

บริการแปลง NetBIOS name → IPv4 ที่ใช้แทน Windows WINS Server: พูด protocol NBNS (RFC 1001/1002) บน UDP 137
ให้เครื่อง/อุปกรณ์ legacy ใช้งานได้เหมือนเดิม แต่เป็น Native AOT `.exe` ไฟล์เดียว (~12 MB) พร้อม web dashboard ในตัว

- Registration / Refresh / Release / Query, ชื่อแบบ unique, group, internet group `<1C>`, multihomed
- Name database ใน memory + TTL หมดอายุอัตโนมัติ, บันทึกลง `wins-db.json` (กลับมาครบหลัง restart)
- Static mapping (แบบ LMHOSTS) ผ่าน dashboard หรือแก้ `static-mappings.json` ตรง ๆ (reload เอง)
- Replication ระหว่าง WinsAlt ด้วยกัน: แต่ละเครื่องดึงสำเนาชื่อของเครื่องอื่นมาเก็บและตอบเองได้ (TCP 8138, ยืนยันด้วย replication key)
- Query forwarding: ชื่อที่ไม่พบจะถูกถามต่อไปยัง WINS server อื่น เช่น Microsoft WINS (ไม่ใช่ replication)
- DNS fallback เมื่อไม่พบชื่อในฐานข้อมูล
- รันเป็น Windows Service หรือ console (มี interactive command)

รายละเอียดการออกแบบ: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), ประวัติการเปลี่ยนแปลง: [CHANGELOG.md](CHANGELOG.md), แผนงาน: [docs/ROADMAP.md](docs/ROADMAP.md)
สัญญาอนุญาต: [MIT](LICENSE), ส่วนประกอบจากภายนอก: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), ขอบเขตการใช้งาน: [installer/usage-terms.th.txt](installer/usage-terms.th.txt)

## ดาวน์โหลดและติดตั้ง

ไฟล์ติดตั้งอยู่ใน[หน้า Releases](../../releases/latest): ตัวติดตั้ง Windows, `.deb`, `.rpm` และ `.tar.gz` สำหรับ Linux
พร้อม `SHA256SUMS.txt` ขั้นตอนตั้งแต่ดาวน์โหลดจนเครื่อง client ใช้งานได้: [คู่มือติดตั้ง (INSTALL.th.md)](INSTALL.th.md)

## ก่อนใช้งานจริงบน Windows - ต้องทำ 3 อย่าง

1. **ปลด UDP 137 จาก NetBT** - Windows ผูก UDP 137 ไว้กับ driver "NetBIOS over TCP/IP" ของทุก adapter ที่เปิด NetBIOS
   ถ้าไม่ปิด WinsAlt จะ bind ไม่ได้ (dashboard ขึ้นสถานะ `Error` และ retry ทุก 15 วินาที)
   วิธีที่ง่ายที่สุด: หน้า Overview ของ dashboard ตาราง "Windows NetBIOS over TCP/IP on this server" กด **Turn off**
   ที่ adapter ที่ client ต่อเข้ามา (service ต้องรันด้วยสิทธิ์ LocalSystem/administrator ซึ่ง installer ตั้งให้)
   หรือปิดเองที่ *Adapter properties → IPv4 → Advanced → WINS → Disable NetBIOS over TCP/IP*
   ถ้า `Wins:ListenAddress` เป็น `0.0.0.0` ต้องปิด**ทุก** adapter; เครื่องที่มีหลาย adapter ให้ตั้ง `ListenAddress`
   เป็น IP ฝั่งที่ให้บริการ แล้วปิดเฉพาะ adapter นั้น
   หลังปิดแล้ว Windows จะไม่ประกาศชื่อเครื่องนี้ผ่าน adapter นั้นอีก WinsAlt จึงประกาศชื่อเครื่องให้เองโดยอัตโนมัติ
   (`Wins:RegisterSelf`) - ถ้า `ListenAddress` เป็น `0.0.0.0` ระเบียนนี้จะตาม IP ของเครื่องเมื่อเปลี่ยน
2. **เปิด firewall ขาเข้า UDP 137** (และ TCP 8138 ถ้าใช้ replication) - installer เพิ่ม rule ใน Windows Firewall ให้
   แต่ firewall/router ระหว่าง site ต้องเปิดเอง และต้องมี route ไปกลับครบทั้งสองทิศ
3. **ชี้ client มาที่เครื่องนี้** - ตั้งค่า WINS server ของ client (DHCP option 44 + option 46 = `0x8` H-node) เป็น IP ของเครื่องนี้

## Build

```powershell
# dev build / ตรวจ compile (เปิด AOT analyzer อยู่แล้ว - IL warning เป็น error)
dotnet build -c Debug -p:PublishAot=false -p:SelfContained=false

# release: Native AOT -> publish\win-x64\WinsAlt.exe
powershell -ExecutionPolicy Bypass -File build-aot.ps1
```

`build-aot.ps1` ต้องมี Visual Studio Build Tools + "Desktop development with C++" (ใช้ `link.exe`)
และตั้ง `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` ให้เอง - `dotnet publish` เปล่า ๆ พังบนเครื่องที่ system culture เป็น th-TH

Dashboard เป็น embedded resource: แก้ `wwwroot/index.html` แล้วต้อง build ใหม่จึงจะเห็นผล

## Run

**Console / interactive** - รัน `WinsAlt.exe` จาก terminal จะมี prompt คำสั่ง: `status`, `names [filter]`, `resolve <name> [suffix]`, `quit`

**ทดสอบโดยไม่ชนพอร์ตจริง** (override ค่าใน appsettings ด้วย environment variable รูปแบบ `Section__Key`):

```powershell
$env:Wins__Port = "11137"; $env:Wins__ListenAddress = "127.0.0.1"
$env:Kestrel__Endpoints__Management__Url = "http://127.0.0.1:18137"
.\publish\win-x64\WinsAlt.exe
# อีกหน้าต่าง: ยิง NBNS packet จริง ตรวจ register/query/refresh/conflict/release
pwsh tools\nbns-smoke.ps1 -Port 11137
```

**Installer (แนะนำ)**

```powershell
powershell -ExecutionPolicy Bypass -File build-installer.ps1   # -> Output\WinsAlt_Setup_v<version>.exe
```

ต้องมี Inno Setup 6 ตัว installer จะ: ลงที่ `Program Files\WinsAlt`, สร้าง service `WinsAlt` (auto-start, restart เมื่อ crash),
เลือกพอร์ต dashboard ที่ว่าง, เพิ่ม firewall rule ขาเข้า UDP 137, TCP 8138 และพอร์ต dashboard (เลือกได้), สร้าง shortcut ไป dashboard (เลือกได้)
แล้วตรวจว่า listener UDP 137 ขึ้นจริงหรือไม่และแจ้งผลตอนจบ การติดตั้งทับ (upgrade) แค่หยุด service เปลี่ยน exe แล้ว start
ไฟล์ข้อมูลทั้งหมดอยู่ครบ - installer **ไม่**ปิด NetBIOS over TCP/IP ให้ ต้องทำเองตามหัวข้อด้านบน
ติดตั้งใหม่ตั้งแต่ 1.6.0: เปิด dashboard ให้เครื่องใน LAN ได้ทันที ต้อง sign in ก่อนเห็นข้อมูล และ sign in ครั้งแรกด้วย **admin / admin**
(หน้าเว็บจะบังคับให้เปลี่ยน) - การติดตั้งทับเก็บ `appsettings.json` เดิมไว้ เครื่องที่ใช้อยู่จึงทำงานเหมือนเดิม
ออกเวอร์ชันใหม่ให้แก้เลขทั้งใน `WinsAlt.csproj` และ `WinsAlt_Setup.iss` (สคริปต์จะหยุดถ้าไม่ตรงกัน)

Installer ยังไม่ได้ sign: Windows Defender อาจเตือนว่าไม่รู้จัก publisher หรือจัดเป็นภัยแบบ heuristic (machine learning)
ถ้าใช้ installer ไม่ได้ ให้ copy `WinsAlt.exe` + `appsettings.json` ไปวางแล้วสร้าง service เองตามด้านล่าง

**Windows Service แบบ manual**

```powershell
sc.exe create WinsAlt binPath= "C:\WinsAlt\WinsAlt.exe" start= auto DisplayName= "WinsAlt NetBIOS Name Server"
sc.exe start WinsAlt
```

Log ไปที่ Windows Event Log (source `WinsAlt`) และแท็บ Events ของ dashboard

### Linux (Debian 13, Ubuntu 26.04 / 24.04, CentOS Stream 10)

ทดสอบแล้วบน **Debian 13** (WSL), **Ubuntu 24.04 / 26.04** (VM จริง, ufw, replication กับ Windows) และ **CentOS Stream 10** (VM จริง, firewalld, SELinux Enforcing) ดู [docs/ROADMAP.md](docs/ROADMAP.md)

```bash
sudo apt install ./winsalt_<version>-1_amd64.deb      # Debian / Ubuntu
sudo dnf install ./winsalt-<version>-1.x86_64.rpm     # CentOS Stream / ตระกูล RHEL
```

หรือแบบไม่ใช้ package manager

```bash
tar -xzf WinsAlt_v<version>_linux-x64.tar.gz
cd winsalt-<version>
sudo ./install.sh            # ติดตั้ง / อัปเกรด แล้ว enable + start service
                             #   --no-start     ติดตั้งอย่างเดียว
                             #   --no-firewall  ไม่เปิด port ใน ufw / firewalld
                             #   --force        ติดตั้งบน distro ที่ไม่อยู่ในรายการ (ไม่ได้ทดสอบ)
```

`.deb` / `.rpm` มี `install.sh` ตัวเดียวกันอยู่ข้างใน และเรียกหลังแตกไฟล์ (ตั้งแต่ 1.8.1) ทั้งสามแบบจึงได้ผลเหมือนกันและอัปเกรดข้ามแบบกันได้

`install.sh` ตรวจก่อนติดตั้ง (ตั้งแต่ 1.6.0):
1. distro ต้องเป็น Debian 13, Ubuntu 24.04 / 26.04 หรือ CentOS Stream 10 และเครื่องต้องเป็น x86_64
2. ติดตั้งสิ่งที่ขาดด้วย apt / dnf: OpenSSL 3 (ใช้ hash รหัสผ่าน), `ss` (iproute2), `curl` แล้วตรวจว่า binary รันบนเครื่องนี้ได้ (`ldd`)
3. port: UDP 137 และ TCP 8138 ต้องว่าง (บอกชื่อโปรแกรมที่ถือไว้ - `nmbd` ของ Samba มีคำแนะนำเฉพาะ);
   ถ้า TCP 8137 ไม่ว่างจะเลือก 8139 / 18137 / 9137 แทนเหมือน installer ของ Windows
4. ถ้า ufw หรือ firewalld **เปิดอยู่แล้ว** จะเปิด UDP 137, TCP 8138 และ port dashboard ให้ (ไม่ติดตั้งหรือเปิด firewall ใหม่)

- ติดตั้งที่ `/opt/winsalt`, ข้อมูลที่ `/var/lib/winsalt`, service `winsalt` (systemd), log: `journalctl -u winsalt -f`
- **Samba `nmbd` ต้องปิดก่อน** (ถือ UDP 137 เหมือน NetBIOS ของ Windows): `sudo systemctl disable --now nmbd` - `smbd` ไม่ต้องปิด
- จบแล้วเปิด `http://<IP ของ server>:8137` จากเครื่องไหนก็ได้ใน LAN แล้ว sign in ด้วย **admin / admin** - หน้าเว็บจะให้เปลี่ยนรหัสทันที
- ถอนการติดตั้งจะลบ rule ของ ufw ที่มี comment "WinsAlt ..." ให้ด้วย (firewalld แสดงรายการ port ให้ปิดเอง)
- ตั้ง hostname ให้เครื่องก่อน (`hostnamectl set-hostname <ชื่อ>`) - ถ้ายังเป็น `localhost` WinsAlt จะไม่ประกาศชื่อตัวเอง
- SELinux (CentOS Stream): ใช้ได้โดยไม่ต้องตั้ง policy เพิ่ม - service รันเป็น `unconfined_service_t`
- ตาราง Firewall ในหน้า Settings ใช้ **firewalld** (CentOS Stream) หรือ **ufw** (Ubuntu / Debian ที่ติดตั้ง ufw) ถ้าไม่มีทั้งสองจะแจ้งว่าไม่มี firewall
- ปุ่ม Restart service ใช้ได้ (systemd start ใหม่ให้)
- service รันเป็น root (ปุ่ม firewall ต้องใช้) โดยถูกจำกัดด้วย sandbox ของ systemd
- ถอนการติดตั้ง: `sudo apt remove winsalt` / `sudo dnf remove winsalt` / `sudo ./uninstall.sh` (เก็บข้อมูลไว้)
  หรือ `sudo apt purge winsalt` / `sudo ./uninstall.sh --purge` (ลบข้อมูลด้วย)
- Build: `powershell -ExecutionPolicy Bypass -File build-linux.ps1` (เรียก `build-linux.sh` ใน WSL) → `Output\WinsAlt_v<version>_linux-x64.tar.gz`,
  `Output\winsalt_<version>-1_amd64.deb`, `Output\winsalt-<version>-1.x86_64.rpm` (สร้างด้วย nfpm ที่ script ดาวน์โหลดให้)
- รวมไฟล์สำหรับ GitHub release พร้อม `SHA256SUMS.txt`: `powershell -ExecutionPolicy Bypass -File tools\release-files.ps1` → `Output\release-<version>\`

> แผนงานต่อไป ดูที่ [docs/ROADMAP.md](docs/ROADMAP.md)

## Dashboard

`http://<IP หรือชื่อเครื่อง server>:8137` - ติดตั้งใหม่ตั้งแต่ 1.6.0 เปิดให้เครื่องใน LAN เข้าได้ทันที
(เครื่องที่อัปเกรดมาจากรุ่นก่อนยังเป็น `http://127.0.0.1:8137` เฉพาะเครื่อง server ตามเดิม)

- **ต้อง sign in ก่อนจึงจะเห็นข้อมูล** (ค่าเริ่มต้น *Sign-in required to view* = ON) ใช้กับทุกเครื่องรวมถึงเครื่อง server เอง
- **sign in ครั้งแรก: `admin` / `admin`** - หลัง sign in หน้าเว็บเปิดหน้าต่างเปลี่ยนรหัสให้ทันที และมีกล่องสีแดงเตือนค้างไว้จนกว่าจะเปลี่ยน
  (รหัสใหม่อย่างน้อย 8 ตัวอักษร) คนที่ยังไม่ sign in จะไม่เห็นว่าเครื่องนี้ยังใช้รหัสเริ่มต้นอยู่
- **ปิด / เปิดการเข้าจากเครื่องอื่น** ทำได้ในแท็บ Settings (sign in ก่อน):
  1. หัวข้อ Dashboard → *Open the dashboard to other computers* → Save → กด **Restart service** ในแถบเตือนด้านบน
     (เทียบเท่าการแก้ host ของ `Kestrel:Endpoints:Management:Url` ระหว่าง `0.0.0.0` กับ `127.0.0.1`)
  2. ตาราง Firewall ท้ายหน้า → แถว "This dashboard" กด **Allow** / **Remove** (มีผลทันที; installer เพิ่มให้แล้วถ้าเลือก task firewall ไว้)

  ถ้ายังเข้าไม่ได้: ตรวจว่าใช้ IP ของ server ถูกตัว และ firewall/router ระหว่างวง network เปิด port ของ dashboard
- **ไม่ sign in = ดูได้อย่างเดียว** (ป้าย *View only* ที่มุมขวาบน) ปุ่มเพิ่ม/แก้ไข/ลบทุกปุ่มถูกซ่อน และ API ปฏิเสธด้วย 401
  การแก้ไขทุกอย่าง (static mapping, ลบ registration, partner, replication, ปุ่ม NetBIOS, replication key, หน้า Settings) ต้อง sign in:
  - ต้อง *Sign in* ด้วยบัญชี `admin` จากทุกเครื่อง รวมถึงเครื่อง server เอง
    (session 8 ชั่วโมง; ผิด 5 ครั้งล็อก IP นั้น 15 นาที; restart service = sign out ทุกคน)
  - ลืมรหัสผ่าน: หยุด service, ลบ `auth.json` (Windows: ข้าง exe, Linux: `/var/lib/winsalt`), start ใหม่ แล้ว sign in ด้วย admin / admin
  - ถ้า `auth.json` เสีย (อ่านไม่ได้) จะ sign in ไม่ได้เลย - ตั้งใจไม่ย้อนกลับไปใช้ admin / admin เอง; แก้ด้วยการลบไฟล์ตามข้อบน
  - สำหรับสคริปต์: ตั้ง *API token for scripts* ในหน้า Settings (`Dashboard:AdminToken`, อย่างน้อย 16 ตัวอักษร) แล้วส่ง header `X-Admin-Token`
- Dashboard เป็น HTTP ธรรมดา ไม่มี TLS - ถ้าเปิดออก LAN ให้จำกัดด้วย firewall
- ถ้า Kestrel bind ไม่ได้ด้วย error 10013 แปลว่าพอร์ตถูกโปรแกรมอื่นถือไว้ หรืออยู่ในช่วงที่ Windows กันไว้ (Hyper-V/WSL):
  ตรวจด้วย `Get-NetTCPConnection -LocalPort 8137` และ `netsh int ipv4 show excludedportrange protocol=tcp` แล้วเปลี่ยนพอร์ต

## ภาษาของ dashboard

หน้าเว็บมีภาษาไทยและอังกฤษ เลือกให้ตามภาษาของ browser ครั้งแรก และเปลี่ยนได้ที่มุมล่างขวา (จำไว้ใน browser)
ข้อความทั้งหมดอยู่ในไฟล์ `wwwroot/lang/<code>.json` ที่ฝังในโปรแกรม

- **เพิ่มภาษาเอง** ไม่ต้อง build ใหม่: คัดลอก `wwwroot/lang/en.json` เป็น `<code>.json` (เช่น `ja.json`) แปลค่า (ไม่แก้ key)
  แล้ววางในโฟลเดอร์ `lang` ของโฟลเดอร์ข้อมูล (Windows: `C:\Program Files\WinsAlt\lang`, Linux: `/var/lib/winsalt/lang`)
  รีเฟรชหน้าเว็บก็เลือกได้ทันที คำที่ไฟล์ไม่มีจะใช้ภาษาอังกฤษแทน
- **แก้คำแปลที่มากับโปรแกรม**: วางไฟล์ชื่อเดียวกัน (`th.json`) ในโฟลเดอร์เดียวกัน จะใช้แทนของในโปรแกรม
- ข้อความใน Events (log) และ error บางข้อความที่มีตัวเลขปน ยังเป็นภาษาอังกฤษ
- รายละเอียดรูปแบบไฟล์: [CONTRIBUTING.md](CONTRIBUTING.md)

## การตั้งค่า

**แก้ได้จากแท็บ Settings ของ dashboard** (ต้อง sign in) ไม่ต้องแก้ไฟล์ `.json` เอง:

- แต่ละหัวข้อมีปุ่ม **?** เปิดหน้าต่างคำอธิบาย (ตามภาษาที่เลือก แบ่งเป็นย่อหน้าและข้อ) ค่าเริ่มต้น ช่วงค่าที่รับ และชื่อ key
- หัวข้อแบบเปิด/ปิดเป็นสวิตช์ **ON / OFF**
- หัวข้อที่มีป้าย **restart** จะมีผลหลัง restart service `WinsAlt` (listen address, port, ขนาดคิว/log, rate limit, ชื่อเครื่องที่ประกาศ);
  หัวข้อที่เหลือ**มีผลทันที** (นโยบายความปลอดภัย, ชื่อต้องห้าม, TTL, conflict policy, DNS, การ sign in เพื่อดู, API token)
  ค่าที่บันทึกแล้วแต่ยังรอ restart จะแสดงเป็นแถบเตือนด้านบนของหน้า พร้อมปุ่ม **Restart service** (กดยืนยันแล้ว service restart เอง
  name server หยุดตอบไม่กี่วินาที และทุกคนถูก sign out)
- ค่าที่ผิดช่วงหรือผิดรูปแบบถูกปฏิเสธพร้อมเหตุผล และไม่มีค่าใดถูกบันทึกจากการกด Save ครั้งนั้น
  (เช่น เลือกโหมด `AllowedSubnets` โดยไม่มี subnet จะไม่ให้บันทึก เพราะจะไม่มีเครื่องใดลงทะเบียนได้)
- ค่าถูกบันทึกลง **`settings.json`** ในโฟลเดอร์ข้อมูล ซึ่งมีลำดับเหนือ `appsettings.json` (ไฟล์ `appsettings.json` ไม่ถูกแก้)
  ลบ `settings.json` แล้ว restart = กลับไปใช้ค่าใน `appsettings.json` ทั้งหมด; environment variable ยังมีลำดับสูงสุด
- ที่ยังต้องแก้ใน `appsettings.json`: พอร์ตของ dashboard (`Kestrel:Endpoints:Management:Url`), `Wins:DataDirectory`,
  `Wins:Workers` และ `Logging`

ตารางด้านล่างคือ key ที่ใช้ใน `appsettings.json` - หน้า Settings แก้ key เดียวกันนี้ ยกเว้น `Wins:Workers` และ `Wins:DataDirectory`:

| Key | ค่าเริ่มต้น | ความหมาย |
|---|---|---|
| `Wins:ListenAddress` / `Wins:Port` | `0.0.0.0` / `137` | จุดฟัง NBNS |
| `Wins:Workers` | `0` | จำนวน worker; 0 = ตามจำนวน core (สูงสุด 4) |
| `Wins:QueueCapacity` | `4096` | คิวต่อ worker; เต็มแล้วทิ้ง packet (นับใน *Dropped*) |
| `Wins:AnswerBroadcasts` | `false` | ตอบ query แบบ broadcast (เฉพาะชื่อที่เจอ) |
| `Wins:MinTtlSeconds` / `Wins:MaxTtlSeconds` | `21600` / `518400` | ช่วง TTL ที่ยอมให้ client |
| `Wins:ConflictPolicy` | `Challenge` | `Challenge` \| `Overwrite` \| `Reject` เมื่อชื่อ unique ถูกขอจาก IP อื่น |
| `Wins:SweepIntervalSeconds` / `Wins:PersistIntervalSeconds` | `30` / `30` | รอบล้างรายการหมดอายุ / รอบบันทึกไฟล์ |
| `Wins:ReplicationPort` | `8138` | พอร์ต TCP ที่ WinsAlt เครื่องอื่นมาดึงสำเนาชื่อ |
| `Wins:RegisterSelf` | `true` | ประกาศชื่อเครื่องนี้เอง (suffix `00` + `20`) ชี้ไปที่ IP ปัจจุบัน และตาม IP ที่เปลี่ยนอัตโนมัติ |
| `Wins:SelfName` | `""` | ชื่อ NetBIOS ที่จะประกาศ (ว่าง = ชื่อคอมพิวเตอร์) |
| `Wins:SelfSuffixes` | `["00","20"]` | suffix ที่ประกาศชื่อเครื่อง (ไม่รับ `1B`/`1C`/`1D`) |
| `Wins:SelfGroup` | `""` | workgroup/domain ที่ลงทะเบียนเครื่องเป็นสมาชิก (ว่าง = ถาม Windows, `"-"` = ไม่ลงทะเบียน) |
| `Wins:DataDirectory` | โฟลเดอร์ของ exe | ที่เก็บ `wins-db.json`, `static-mappings.json`, `partners.json`, `replication.json`, `auth.json`, `settings.json` |
| `Wins:Dns:Enabled` | `true` | DNS fallback |
| `Wins:Dns:Servers` | `[]` | DNS server ที่จะถามตรง (ว่าง = resolver ของ OS) |
| `Wins:Dns:Suffix` | `""` | ต่อท้ายชื่อก่อนถาม DNS เช่น `corp.local` |
| `Wins:Dns:TimeoutMs` / `CacheSeconds` / `NegativeCacheSeconds` | `2000` / `300` / `60` | |
| `Dashboard:AdminToken` | `""` | token สำหรับสคริปต์ที่ต้องแก้ไขผ่าน API (header `X-Admin-Token`) |

### `static-mappings.json`

```json
{
  "mappings": [
    { "name": "SERVER01", "addresses": ["192.168.1.10"], "comment": "ตัวอย่าง" },
    { "name": "CORP", "addresses": ["192.168.1.2", "192.168.1.3"], "suffixes": ["1C"], "group": true }
  ]
}
```

ไม่ใส่ `suffixes` = `00` (workstation) + `20` (file server)

## ความปลอดภัย (`Wins:Security`)

NBNS ไม่มีการยืนยันตัวตน: เครื่องใดที่ส่ง packet ถึง UDP 137 ได้ก็ลงทะเบียนชื่อได้ ซึ่งเป็นช่องทางของการ poisoning
(เช่น Responder) ค่าเริ่มต้นของ WinsAlt เข้ากันได้กับ WINS เดิมและเปิดเฉพาะการป้องกันที่ไม่กระทบเครื่องปกติ
ส่วนการจำกัดว่าใครลงทะเบียนได้ต้องเปิดเอง ในแท็บ Settings หัวข้อ Security (มีผลทันที ยกเว้น rate limit และจำนวน challenge ที่ต้อง restart)
หน้า Overview มี panel **Security policy** แสดงค่าที่ใช้อยู่และจำนวนครั้งที่ถูกปฏิเสธ

| Key | ค่าเริ่มต้น | ความหมาย |
|---|---|---|
| `RegistrationMode` | `Dynamic` | `Dynamic` = ใครก็ลงทะเบียนได้, `AllowedSubnets` = เฉพาะ subnet ที่กำหนด, `StaticOnly` = ปิด dynamic ทั้งหมด |
| `AllowedSubnets` | `[]` | รายการ CIDR เช่น `"192.168.10.0/24"` - ทั้ง IP ผู้ส่งและ IP ที่ลงทะเบียนต้องอยู่ในรายการ |
| `RequireAddressMatchesSource` | `false` | ลงทะเบียนได้เฉพาะ IP ของตัวเอง (อย่าเปิดถ้ามีเครื่องที่ต่อทั้ง LAN และ Wi-Fi หรืออยู่หลัง NAT) |
| `BlockedNames` | `["WPAD","ISATAP"]` | ชื่อที่ลงทะเบียนแบบ dynamic ไม่ได้ และไม่ถูกถามต่อไป partner/DNS (static mapping ยังตอบได้) |
| `MaxDynamicNames` / `MaxNamesPerAddress` | `100000` / `64` | เพดานจำนวนชื่อทั้งฐานข้อมูล / ต่อหนึ่ง IP (0 = ไม่จำกัด) |
| `RateLimitPerSecond` / `RateLimitBurst` | `100` / `300` | จำนวน request ต่อวินาทีต่อหนึ่ง IP ต้นทาง (0 = ปิด); partner server ได้รับยกเว้น |
| `MaxConcurrentChallenges` | `64` | จำนวน name challenge ที่ทำพร้อมกันได้ |
| `AllowMultihomedMerge` | `true` | ยอมให้ชื่อ unique มี IP ที่สองเมื่อเจ้าของเดิมยืนยัน (เครื่องที่ต่อ LAN + Wi-Fi) |

ผลของการถูกปฏิเสธ: client ได้ RCODE 5 (refused) และมีบรรทัดในแท็บ Events / Query log บอกเหตุผล

**Cache poisoning** - WinsAlt เก็บ cache สองที่: คำตอบจาก partner server และจาก DNS (5 นาที) การป้องกัน:
transaction id สุ่มแบบเดาไม่ได้ + port ต้นทางสุ่มใหม่ทุกครั้ง + ต้องมาจาก IP ของ partner + ชื่อต้องตรง, คำตอบ DNS ต้องมีคำถามตรงกับที่ถาม,
IP ที่ไม่ใช่เครื่องจริง (0.x, 127.x, multicast, broadcast) ไม่ถูกเก็บ, ชื่อใน `BlockedNames` ไม่ถูกถามต่อเลย, อายุ cache สั้น,
และปุ่ม **Clear cache** ในแท็บ Partner servers สำหรับล้างทันที สิ่งที่กันไม่ได้: ผู้โจมตีที่อยู่บนเส้นทางระหว่าง server กับ partner/DNS
(อ่าน packet ได้) และข้อมูลผิดที่ partner เก็บไว้เอง - ชื่อสำคัญจึงควรเป็น static mapping ซึ่งถูกตอบก่อนทุกแหล่ง

**แนะนำสำหรับ network ที่รู้ขอบเขตชัดเจน**: `RegistrationMode: AllowedSubnets` พร้อมรายการ subnet ของ client ทั้งหมด
ก่อนเปิดให้ดูหน้า Registered names ว่ามี IP ช่วงไหนลงทะเบียนอยู่จริง - ใส่ไม่ครบ = เครื่องในช่วงที่ขาดลงทะเบียนไม่ได้

ฝั่ง dashboard: ตรวจ Host header (กัน DNS rebinding), ทุกคำสั่งแก้ไขต้องมี header `X-Requested-With: WinsAlt` (กัน CSRF),
security header ครบ, จำกัดขนาด request; `Dashboard:RequireSignInToView: true` (ค่าเริ่มต้นตั้งแต่ 1.6.0) บังคับ sign in แม้แค่ดูข้อมูล จากทุกเครื่องรวมถึงเครื่อง server;
`Dashboard:AllowedHosts` สำหรับชื่อ DNS ที่ใช้เปิด dashboard

ทดสอบทุกข้อ: `pwsh tools\security-smoke.ps1`, วัดความเร็ว: `WinsAlt.exe --bench 127.0.0.1 <port> 10`
(ตั้ง `Wins__Security__RateLimitPerSecond=0` ให้ server ที่จะวัด)

## Replication และ query forwarding (แท็บ Partner servers)

**Replication - ระหว่าง WinsAlt ด้วยกัน** แต่ละเครื่องดึงสำเนาชื่อทั้งหมดที่เครื่องอื่นเป็นเจ้าของทุก 30 วินาที และตอบ client
ได้เองแม้ site อื่นติดต่อไม่ได้

1. ตั้ง **replication key** เดียวกันทุกเครื่อง (12 ตัวอักษรขึ้นไป; ไม่ตั้ง = ปิด replication) ปุ่ม Show key ใช้ดู key เพื่อ copy
2. เพิ่ม IP ของเครื่องอื่นใน "Add server" - **ต้องเพิ่มกันและกันครบทุกคู่** (แต่ละเครื่องดึงอย่างเดียว ไม่ส่งต่อสำเนาของเครื่องที่สาม)
3. เปิด TCP 8138 ระหว่าง site ทั้งสองทิศ - แต่ละเครื่องรับการเชื่อมต่อเฉพาะจาก IP ที่อยู่ในรายการของตัวเอง
   จึงต้องเพิ่มครบทั้งสองฝั่งก่อนจะ sync ได้ (ฝั่งที่เพิ่มก่อนจะขึ้น error จนอีกฝั่งเพิ่มกลับ แล้วหายเองภายใน 30 วินาที)

ชื่อที่ลงทะเบียนในเครื่องตัวเองชนะสำเนาเสมอ; ชื่อที่ถูกปล่อยหรือหมดอายุที่ต้นทางหายไปในรอบดึงถัดไป; `<1C>` รวมสมาชิกจากทุก site

**Query forwarding - ไปยัง WINS server ใดก็ได้ (เช่น Microsoft WINS)** ชื่อที่ไม่พบจะถูกถามต่อด้วย name query บน UDP 137
แล้วส่งคำตอบให้ client (cache 5 นาที) เป็นทางเดียว: ชื่อที่ลงทะเบียนที่นี่ไม่ถูกคัดลอกไปให้ partner

ลำดับการตอบ client - **static mapping มาก่อนเสมอ**: static ในเครื่อง → static จาก replication → ชื่อที่ลงทะเบียนในเครื่อง →
ชื่อจาก replication → partner → DNS หน้า Registered names มีคอลัมน์ **Source**
บอกว่าแต่ละชื่อมาจาก Local, Replication หรือ Partner

**ชื่อเดียวกันอยู่ทั้งสอง server คนละ address**: replication ไม่ลบข้อมูลที่ลงทะเบียนกับเครื่องตัวเอง (ลองทำใน 1.7.0-1.7.2
แล้วถอนออก - client ในเครือข่ายนี้ต่ออายุกับทั้งสอง server และบางเครื่องลงทะเบียนคนละการ์ดกับคนละ server) IP เก่าที่ค้างหลัง
client ย้าย VLAN จะหมดอายุเอง หรือลบชื่อนั้นในหน้า Registered names ของ server ที่ถือ IP เก่า - คำตอบจะมาจากสำเนาของอีกเครื่องทันที
และ client ลงทะเบียนกลับเข้ามาด้วย IP ปัจจุบันในรอบต่ออายุถัดไป

ทดสอบ replication บนเครื่อง dev: `pwsh tools\replication-smoke.ps1` (เปิด WinsAlt สองตัวเอง)

## ผลของการปิด NetBIOS over TCP/IP บนเครื่อง server

WinsAlt ต้องการ UDP 137 จึงต้องปิด NetBIOS ของ Windows บน adapter นั้น ซึ่งปิดทั้ง port 137, 138 และ 139
Client ที่มาใช้ WINS ไม่ได้รับผลกระทบ แต่ตัวเครื่อง server เองเปลี่ยนไปดังนี้

| สิ่งที่ Windows เคยทำ | หลังปิด |
|---|---|
| ประกาศชื่อเครื่อง `<00>` `<20>` | WinsAlt ประกาศแทน (`Wins:RegisterSelf`, `SelfSuffixes`) |
| เป็นสมาชิก workgroup/domain `<00>` | WinsAlt ลงทะเบียนแทน (`Wins:SelfGroup`) |
| ตอบ `nbtstat -A` / เครื่องมือสแกน | WinsAlt ตอบแทน |
| ตอบ broadcast หาชื่อเครื่องนี้ | เปิด `Wins:AnswerBroadcasts: true` ถ้ามีอุปกรณ์ที่ไม่ได้ตั้ง WINS |
| SMB ผ่าน TCP 139 | **ใช้ไม่ได้** - file sharing ผ่าน 445 ยังปกติ; อุปกรณ์เก่าที่ต่อได้แค่ 139 จะเข้า share บนเครื่องนี้ไม่ได้ |
| Network Neighborhood / mailslot (UDP 138) | **ใช้ไม่ได้** |
| เครื่อง server เองถาม WINS / LMHOSTS | **ใช้ไม่ได้** - โปรแกรมบนเครื่องนี้หาชื่อผ่าน DNS และไฟล์ hosts เท่านั้น |
| ชื่อบทบาท `1B` `1C` `1D` | ไม่ประกาศ - อย่าติดตั้งบน domain controller เว้นแต่เพิ่ม static mapping เอง |

ถ้าสามแถว "ใช้ไม่ได้" กระทบงานจริง ให้รัน WinsAlt บนเครื่องหรือ VM แยกแล้วย้าย IP ของ WINS ไปเครื่องนั้น

## ข้อจำกัด

- ใช้งานจริงแล้วบน Windows 10, Windows 7 และ Ubuntu 26.04;
  Windows 7 อยู่นอกรายการที่ .NET 10 รองรับ รันได้แต่ไม่มีการรับประกัน
- Replication ใช้ได้เฉพาะระหว่าง WinsAlt ด้วยกัน (ต้องเพิ่มกันและกันครบทุกคู่ และใช้ replication key เดียวกัน;
  ข้อมูลบนสายไม่ได้เข้ารหัส และ key เก็บเป็นข้อความธรรมดาใน `replication.json`)
- กับ Microsoft WINS ทำได้แค่ query forwarding ทางเดียว: ชื่อที่ลงทะเบียนที่นี่**ไม่ถูกคัดลอก**ไปให้
  (replication แบบ push/pull ของ Microsoft WINS - MS-WINSRA บน TCP 42 - เป็นฟีเจอร์ในอนาคต)
- ไม่รองรับ NetBIOS scope ID และ name service บน TCP 137; node status ตอบเฉพาะของเครื่อง server เอง
- NBNS ไม่มีการยืนยันตัวตนโดยธรรมชาติ และ IP ต้นทางของ UDP ปลอมได้: มาตรการในหัวข้อ "ความปลอดภัย" ลดความเสี่ยงแต่ไม่ได้ทำให้
  protocol ปลอดภัย - ชื่อสำคัญควรทำเป็น static mapping ซึ่ง client เขียนทับไม่ได้
- DNS fallback ทำให้ชื่อ single-label ใด ๆ ที่ DNS ตอบได้ กลายเป็นชื่อ NetBIOS ที่ resolve ได้ ปิดได้ด้วย `Wins:Dns:Enabled=false`
