# WinsAlt - Architecture Overview

WinsAlt คือ NetBIOS Name Server (NBNS) ที่ทำหน้าที่แทน WINS: รับ Registration / Refresh / Release / Query
ตาม RFC 1001/1002 บน UDP 137 แล้วตอบด้วย wire format เดิม เครื่อง legacy จึงใช้งานได้โดยแค่ชี้ค่า
"WINS server" มาที่เครื่องนี้ สิ่งที่ตัดทิ้งคือ legacy bloat ของ WINS จริง: Jet database, tombstone/extinction,
version vector ของ replication, MMC snap-in (replication มีในรูปแบบที่ง่ายกว่า - ข้อ 8)

ทั้งหมดเป็น **single Native AOT executable** (~12 MB) รันเป็น Windows Service หรือ console ก็ได้

## 1. รูปแบบที่นำมาจากโปรเจกต์ dashboard ก่อนหน้า

| Pattern จากโปรเจกต์ก่อนหน้า | ใช้ใน WinsAlt ที่ไหน |
|---|---|
| `WebApplication.CreateSlimBuilder` + `ContentRootPath = AppContext.BaseDirectory` + `UseWindowsService` | `Program.cs` - binary เดียวเป็นได้ทั้ง service และ console |
| JSON ผ่าน source generator เท่านั้น (`ApiJsonContext`, `ConfigJsonContext`), ปิด reflection JSON ใน csproj | `Web/JsonContexts.cs` - `ApiJsonContext` (API), `ConfigJsonContext` (static-mappings.json), `DbJsonContext` (wins-db.json) |
| Dashboard เป็น `<EmbeddedResource>` (index.html + Vue runtime) เสิร์ฟจาก manifest resource | `wwwroot/` + `Web/EmbeddedAssets.cs` |
| `ForwardingManager : BackgroundService` + self-heal retry ทุก 15 วินาทีเมื่อ bind ไม่ได้ | `Network/NbnsServer.cs` - สถานะ `Error` + `LastError` โชว์บน dashboard |
| UDP session sweeper ด้วย `PeriodicTimer` | `Network/NameMaintenanceService.cs` - expiry sweep + persistence flush |
| `SIO_UDP_CONNRESET` กัน ICMP unreachable ทำ receive loop พัง | `NbnsServer.DisableConnReset` |
| Hot path ห้าม allocate ต่อ packet; ตัวนับใช้ `Interlocked` | `WinsCounters`, `WorkerContext`, `NameStore.Query` |
| `ConfigService`: lock + atomic write (tmp แล้ว `File.Move` ทับ) + `FileSystemWatcher` | `Infrastructure/StaticMappingService.cs`, `WinsDatabase.cs` |
| `InMemoryLogStore` + `InMemoryLoggerProvider` (ring 500 รายการ) | `Infrastructure/InMemoryLogStore.cs` - แท็บ Events |
| `MetricsService` (timer 2 วินาที, CPU/RAM, history ring) | `Infrastructure/MetricsService.cs` - เพิ่มอัตรา query/registration ต่อวินาที |
| Minimal API `MapGroup("/api")` + endpoint filter สำหรับ mutation | `Web/ApiEndpoints.cs` - `RequireAdmin` |
| `build-aot.ps1` (invariant culture + vswhere บน PATH) | `build-aot.ps1` |

สิ่งที่**ไม่ได้**นำมา: `AuthService` (PBKDF2 + DPAPI + session cookie), edition/licence (`DISTRIBUTION`,
`SingleInstanceGuard`), Inno Setup installer เพราะ prompt ไม่ได้ขอ - ส่วน auth แทนด้วยกฎที่เบากว่า (ข้อ 6)
และทำให้ target เป็น `net10.0` ล้วนได้ (ไม่ต้อง `-windows`)

## 2. โครงสร้างโปรเจกต์

โปรเจกต์เดียว (`WinsAlt.csproj`) แบ่งโมดูลด้วยโฟลเดอร์/namespace - เหมือนต้นแบบ และไม่เพิ่ม trim surface ข้าม assembly

```
Core/Protocol/      NbnsHeader, NbnsName, NbnsPacketReader, NbnsPacketWriter, NbnsPackets   (wire format ล้วน)
Core/Domain/        NameKey, NameRecord, Ipv4, WinsOptions                                   (ไม่มี I/O)
Infrastructure/     NameStore, WinsDatabase, StaticMappingService, DnsFallbackResolver,
                    ReplicaStore, ReplicationService, PartnerService, NetbtAdapterService,
                    QueryLog, InMemoryLogStore, MetricsService, WinsCounters
Network/            NbnsServer, NbnsRequestHandler, NameChallengeService, NameMaintenanceService,
                    ReplicationWorkers (server + client), SelfRegistrationService
Web/                ApiEndpoints, JsonContexts, EmbeddedAssets
wwwroot/            index.html (Vue 3 SPA ไฟล์เดียว), vue.global.prod.js
Program.cs          host + DI          ConsoleCommandService.cs   CLI interactive mode
InstallerCommands.cs   --pick-port / --listener-state สำหรับ installer
```

ทิศทาง dependency: `Web` / `Network` → `Infrastructure` → `Core`

## 3. เส้นทางของ packet UDP 137

```
                    ┌────────────── NbnsServer (BackgroundService) ──────────────┐
 UDP 137 ──recv──▶  │ ReceiveLoop ─hash(source IP)─▶ Channel[0] ─▶ Worker 0 ──┐  │
 (socket เดียว)      │  (1 task)                     Channel[1] ─▶ Worker 1 ──┼──┼─ SendTo ─▶ client
                    │                               Channel[N] ─▶ Worker N ──┘  │
                    └──────────────────────────────────┬─────────────────────────┘
                                                       ▼
                                NbnsRequestHandler.Process(ReadOnlySpan<byte>)
                                   │ parse (NbnsMessage.TryParse)
                                   ├─ Query ───────▶ NameStore ─miss▶ ReplicaStore ─miss▶ partner cache ─▶ DNS cache
                                   │                                              (cache ยังไม่รู้ = task: ถาม partner แล้ว DNS)
                                   ├─ Register/Refresh ▶ NameStore.Register ──conflict──▶ task: challenge เจ้าของเดิม
                                   └─ Release ─────▶ NameStore.Release
```

1. **Receive loop (1 task)** ทำอย่างเดียวคือดึง datagram ออกจาก socket ลง buffer ที่เช่าจาก `ArrayPool<byte>`
   โดยใช้ `ReceiveFromAsync(Memory<byte>, SocketFlags, SocketAddress)` กับ `SocketAddress` ตัวเดียวที่ reuse
 - ไม่มี `IPEndPoint`/`IPAddress` ใหม่ต่อ packet; IP/port ต้นทางอ่านตรงจาก sockaddr เป็น `uint`/`ushort`
2. **Routing ด้วย hash ของ source IP** → packet ของ client เดียวกันเข้า worker เดียวกันเสมอ ลำดับจึงคงเดิม
   (register แล้ว query ทันทีไม่ race) ส่วน client ต่างกันกระจายข้าม core
3. **Bounded `Channel<Datagram>`** (`Datagram` เป็น struct) เขียนด้วย `TryWrite` เท่านั้น: คิวเต็ม = ทิ้ง + นับ `Dropped`
   NBNS client retransmit เองอยู่แล้ว การทิ้งจึงดีกว่าตอบช้าจน client timeout ไปก่อน
4. **Worker** เรียก `Process` แบบ synchronous: parse ด้วย `ref struct` reader, lookup, เขียนคำตอบลง buffer ประจำ worker
   แล้วส่งด้วย `Socket.SendTo(ReadOnlySpan<byte>, SocketFlags, SocketAddress)` - UDP send แค่ copy ลง kernel buffer
   จึงไม่ต้องมี async state machine
5. **Slow path** ที่ต้องรอ network จะแตกไปเป็น task (allocate ได้ เพราะเกิดน้อย): ถาม partner WINS, DNS fallback
   และ name challenge
6. Socket ผูกแบบ **exclusive** (`ExclusiveAddressUse`): Windows ยอมให้ bind `0.0.0.0:137` ซ้อนกับ NetBT ที่ถือ `<ip>:137`
   ได้ ซึ่งทำให้ listener ดูปกติแต่ได้รับแต่ broadcast - การ bind แบบ exclusive ทำให้กรณีนั้นล้มเหลวและรายงานเป็น Error

### สถานะ zero-allocation

| เส้นทาง | Allocation ต่อ packet |
|---|---|
| Query hit / miss / static hit / replica hit / partner cache hit / DNS cache hit | ไม่มี |
| Refresh โดยเจ้าของเดิม | ไม่มี (เขียน expiry ทับใน member array เดิม) |
| Registration ใหม่ | `NameRecord` + member array 1 ชุด + log event 1 บรรทัด |
| Partner / DNS lookup ครั้งแรกของชื่อ, name challenge | task + buffer เล็ก ๆ (slow path) |

สิ่งที่ทำให้ hot path ไม่ allocate: `NameKey` (ชื่อ 16 byte เก็บเป็น `ulong` 2 ตัว decode จาก wire ตรง ๆ ไม่ผ่าน string),
IPv4 เป็น `uint`, `QueryLog` เป็น ring ของ struct (แปลงเป็น string ตอน dashboard มาอ่านเท่านั้น),
ตัวนับเป็น `Interlocked.Increment`

## 4. Name database

- `NameStore` = `ConcurrentDictionary<NameKey, NameRecord>` - **อ่านไม่ lock** (query คือ traffic ส่วนใหญ่)
  ส่วนการเขียนทั้งหมด serialize ด้วย lock เดียว เพราะ client แตะชื่อของตัวเองแค่ครั้งต่อ renewal interval
- ชนิดชื่อ: `Unique`, `Group` (สมาชิกสูงสุด 25), `Multihomed`; สมาชิกแต่ละ address มี expiry ของตัวเอง
- **TTL**: ใช้ค่าที่ client ขอ บีบให้อยู่ใน `MinTtlSeconds`..`MaxTtlSeconds` (ค่าเริ่มต้น 6 ชั่วโมง - 6 วัน ตาม WINS)
  รายการที่หมดอายุถูกมองข้ามทันทีตอน query; sweep ทุก 30 วินาทีมีไว้คืน memory และบันทึก event
- **Static mapping** (`static-mappings.json`, แก้มือได้/แก้ผ่าน dashboard ได้) ไม่หมดอายุ และ client ลงทะเบียนทับไม่ได้
- **Persistence** (`wins-db.json`): snapshot ทั้งตารางเมื่อมีการเปลี่ยนแปลง (ทุก 30 วินาที + ตอน stop) แบบ atomic
  เลือก JSON snapshot แทน SQLite เพราะไม่มี native dependency/trim warning และข้อมูล WINS self-heal อยู่แล้ว
  (เจ้าของชื่อ refresh เองภายใน TTL)

## 5. พฤติกรรม protocol ที่ตัดสินใจไว้

| เรื่อง | พฤติกรรม |
|---|---|
| ชื่อ unique ถูกลงทะเบียนจาก IP อื่น | ค่าเริ่มต้น `Challenge` แบบ WINS: ตอบ WACK ให้ผู้ขอรอ → ส่ง name query ไปหาเจ้าของเดิม (3 ครั้ง × 500 ms) → เจ้าของตอบ = ปฏิเสธ `ACT_ERR`; เงียบ = ยกชื่อให้ผู้ขอ (กรณี DHCP เปลี่ยน IP); **คำตอบของเจ้าของมี IP ของผู้ขออยู่ด้วย = เครื่องเดียวกันสอง adapter → เก็บทั้งสอง IP เป็น Multihomed** ตั้ง `ConflictPolicy` เป็น `Overwrite`/`Reject` ได้ |
| Group ปกติ | query ได้ `255.255.255.255`; มีแต่ internet group `<1C>` ที่คืนรายชื่อสมาชิก |
| `<1D>` (master browser ราย segment) | ตอบรับแต่ไม่เก็บ เหมือน WINS |
| Release ชื่อที่ไม่ใช่ของตัวเอง | `ACT_ERR`; release ชื่อที่ไม่มีอยู่ = ตอบรับ |
| Packet ที่มี B flag (broadcast) | **เมินโดยค่าเริ่มต้น** เหมือน WINS ที่รับแต่ unicast เปิด `AnswerBroadcasts` แล้วจะตอบเฉพาะ query ที่ "เจอ" เท่านั้น ไม่ตอบ negative และไม่รับ registration จาก broadcast |
| DNS fallback | เฉพาะ suffix `00`/`20` และชื่อที่เป็น hostname ได้; ผลลัพธ์ (ทั้งเจอ/ไม่เจอ) ถูก cache; ตั้ง `Dns:Servers` เพื่อยิง UDP 53 ตรง ไม่ตั้ง = ใช้ resolver ของ OS |
| Negative query response | ที่ส่งออก: RCODE `NAM_ERR` + record ชนิด NULL ที่ RDATA ว่าง โดยตั้ง ANCOUNT=1; ที่รับเข้า: รองรับรูปแบบของ Microsoft WINS ด้วย (ทุก count เป็น 0 แล้วตามด้วยชื่อ) |
| ชื่อเครื่องของ server เอง | ประกาศเองอัตโนมัติ (suffix `00`+`20`, เพิ่มได้ด้วย `SelfSuffixes`) เพราะเมื่อปิด NetBT แล้ว Windows ไม่ประกาศให้; ตาม IP เมื่อเปลี่ยน - ดู 5.1 |
| Node status (NBSTAT, `nbtstat -A`) | ตอบแทนเครื่องที่รันอยู่เท่านั้น: เฉพาะ unicast และเฉพาะคำถามถึงชื่อ `*` หรือชื่อของเครื่องเอง; คำถามถึงชื่อเครื่องอื่นไม่ได้คำตอบ (นับเป็น Unsupported) |

**ไม่รองรับ**: NetBIOS scope ID (packet ที่มี scope ถูกเมิน), node status แทนเครื่องอื่น, replication กับ Microsoft WINS
(MS-WINSRA บน TCP 42), NetBIOS name service บน TCP 137, IPv6 (NetBIOS เป็น IPv4 เท่านั้น)

### 5.1 ตัวแทนของเครื่อง server เมื่อปิด NetBT (`SelfRegistrationService`)

เมื่อปิด NetBIOS over TCP/IP เพื่อให้ service ได้ UDP 137 เครื่อง server จะไม่มีตัวตนบน NetBIOS เลย service นี้ทำแทนสามอย่าง:

- **ชื่อเครื่อง** - เก็บเป็น static แบบ automatic (`StaticMappingService.SetAutomaticEntries`, comment "This server (automatic)")
  หนึ่งรายการต่อ suffix ใน `Wins:SelfSuffixes`; หลาย IP = Multihomed. `1B`/`1C`/`1D` ถูกตัดออกตอนอ่าน config
  เพราะเป็นบทบาท (domain master browser, domain controller, master browser) ที่ service นี้ไม่ได้ทำ
- **สมาชิก workgroup/domain** (`<กลุ่ม><00>` แบบ group) - เป็น registration **dynamic** ธรรมดาใน `NameStore`
  (อยู่ในรายชื่อสมาชิกร่วมกับ client) ต่ออายุทุกรอบจึงไม่หมดอายุ; ชื่อกลุ่มมาจาก `Wins:SelfGroup` หรือถาม Windows (`"-"` = ไม่ลงทะเบียน)
- **Node status** - RDATA ตาม RFC 1002 §4.2.18 (จำนวนชื่อ, 18 byte ต่อชื่อ, แล้ว statistics 46 byte ที่ขึ้นต้นด้วย MAC ของ adapter แรก)
  ถูกสร้างไว้ล่วงหน้าเป็น `byte[]` และสร้างใหม่เฉพาะเมื่อ IP เปลี่ยน - เส้นทาง packet แค่ copy (`NbnsPackets.WriteNodeStatusResponse`) ไม่ allocate

ทั้งหมดถูกคำนวณใหม่เมื่อ Windows แจ้งว่า address เปลี่ยน (`NetworkChange.NetworkAddressChanged`) และตาม timer เป็นตัวสำรอง
IP ที่ใช้: ถ้า `ListenAddress` เป็น IP เฉพาะใช้ IP นั้น; ถ้าเป็น `0.0.0.0` ใช้ adapter ที่มี default gateway

**ทำแทนไม่ได้** (เป็นหน้าที่ของ driver ใน Windows): datagram service UDP 138 (Network Neighborhood, mailslot), SMB ผ่าน TCP 139,
และการที่เครื่อง server เองใช้ WINS หาชื่อ

## 6. Dashboard / API

- รันได้ทั้ง Windows (Service) และ Linux (systemd, ตั้งแต่ 1.5.0) จาก source เดียว - จุดที่ต่างกันตาม OS ดู [ROADMAP.md](ROADMAP.md) ข้อ 1
- Kestrel bind ค่าเริ่มต้น `0.0.0.0:8137` (appsettings.json ตั้งแต่ 1.6.0; เดิม `127.0.0.1`) - GET ข้อมูลต้อง sign in เมื่อ
  `Dashboard:RequireSignInToView` เปิด (ค่าเริ่มต้นตั้งแต่ 1.6.0, `MayView` - ไม่ยกเว้น loopback เพราะ reverse proxy บนเครื่องเดียวกัน
  จะทำให้ทุกคนดูเหมือน loopback); `/api/info` และ `/api/me` เปิดเสมอ; **mutation** ต้องเป็น admin (`ApiEndpoints.IsAdmin`):
  session cookie จากการ sign in (`AuthService`: PBKDF2-SHA256 ใน `auth.json`, session ใน memory, ล็อก IP เมื่อเดารหัส)
  หรือ header `X-Admin-Token` เท่านั้น - **ผู้ที่ไม่ได้ sign in ดูได้อย่างเดียว รวมถึงเครื่อง server เอง**
  ไม่มี `auth.json` = ใช้รหัสเริ่มต้น admin / admin (`AuthService.UsingDefaultPassword`); `/api/me` ตอบ `defaultPassword`
  เฉพาะกับ admin เท่านั้น (ไม่บอกคนนอกว่ารหัสเริ่มต้นใช้ได้) และหน้าเว็บบังคับเปิดหน้าต่างเปลี่ยนรหัส + กล่องเตือนสีแดง;
  `auth.json` ที่อ่านไม่ได้ = ไม่มีใคร sign in ได้ (fail closed) แก้ด้วยการลบไฟล์;
  หน้าเว็บซ่อนปุ่มแก้ไขทั้งหมดเมื่อ `me.admin` เป็น false (`canEdit`) - server ปฏิเสธอยู่แล้ว
  (setup mode ของรุ่นก่อน 1.6.0 - ตั้งรหัสแรกจาก loopback - ถูกเอาออกแล้ว)
- หน้าตา dashboard ใช้ design เดียวกับโปรเจกต์ก่อนหน้า (token สี, panel, ตาราง, modal, icon sprite, ฟอนต์ฝัง)
- Endpoints อ่าน: `/api/info`, `/api/me`, `/api/status`, `/api/metrics/history`, `/api/names`, `/api/resolve`,
  `/api/querylog`, `/api/logs`, `/api/static`, `/api/partners`, `/api/replication`, `/api/netbt`
- Endpoints แก้ไข (admin): `/api/static`, `/api/static/{name}`, `/api/names/{id}`, `/api/partners`,
  `/api/partners/{address}`, `/api/replication/key` (GET ด้วย), `/api/replication/peers`,
  `/api/replication/peers/{address}`, `/api/netbt/{id}`, `/api/cache/clear` (ล้าง cache ของ partner และ DNS)
- `/api/settings`: GET = รายการ setting ทั้งหมดพร้อมคำอธิบาย (ดู 6.1), PUT (admin) = บันทึกเฉพาะ key ที่ส่งมา
- `POST /api/service/restart` (admin) - ดู 6.3
- `/api/firewall`: GET = สถานะ rule ทั้งสาม, `POST /api/firewall/{id}?allow=true|false` (admin) (ดู 6.2)
- `/api/status` มี `self` (`enabled`, `name`, `addresses`, `group`, `suffixes`) - หน้า Overview แสดงเป็นบรรทัด "Published as"

### 6.1 Settings จาก dashboard (`SettingsService`, `SettingsOverlay`)

- **ที่เก็บ**: `settings.json` ในโฟลเดอร์ข้อมูล เป็น map แบน `"config key": "value"` และเป็น configuration source ตัวหนึ่ง
  (`SettingsOverlay`) ที่ `Program.cs` แทรกไว้**เหนือ `appsettings.json` แต่ใต้ environment variable** - `appsettings.json` ไม่ถูกเขียนทับ
  (installer ยังแก้พอร์ต dashboard ในไฟล์นั้นได้ และ instance ทดสอบหลายตัวที่ใช้ exe เดียวกันไม่ชนกัน)
- **Catalogue** (`SettingsService.Catalogue`): key ที่แก้ได้, ชนิด, ช่วงค่า, คำอธิบาย และ `Hot` หรือไม่ - key นอก catalogue เขียนไม่ได้
  ไม่ว่า request จะส่งอะไรมา (`Wins:DataDirectory`, `Kestrel:*`, `Logging:*` จึงแก้จากเว็บไม่ได้)
- **บันทึก**: ตรวจทีละค่า (ปฏิเสธ ไม่ clamp) → ใส่ลง overlay → อ่าน `WinsOptions.FromConfiguration` ใหม่ทั้งชุดเหมือนตอน restart →
  ตรวจกฎข้ามค่า (min ≤ max TTL, `AllowedSubnets` ต้องมี subnet, burst ≥ rate, ค่าที่ถูก environment variable ทับ) →
  เขียนไฟล์แบบ atomic → `WinsOptions.ApplyHot` ถ้าขั้นใดไม่ผ่าน overlay ถูกคืนค่าเดิมและไม่มีอะไรถูกบันทึก
- **Hot vs restart**: ค่าที่โค้ดอ่านจาก `WinsOptions` ทุกครั้งที่ใช้ (นโยบายลงทะเบียน, ชื่อต้องห้าม, quota, TTL, conflict policy,
  broadcast, DNS, dashboard) ถูก copy เข้า instance เดิมทันที - array/set ถูกแทนทั้งก้อน ไม่แก้ในที่ เส้นทาง packet จึงยังไม่ lock ไม่ allocate
  ค่าที่ถูกแปลงเป็น socket/buffer/timer ตอน start (listen address, port, คิว, ขนาด log, rate limiter, challenge, ชื่อเครื่อง) ต้อง restart;
  `Describe()` เทียบค่าที่บันทึก (`_saved`) กับค่าที่รันอยู่ (`_live`) แล้วรายงานเป็น `pendingRestart`
- **List** (subnet, ชื่อต้องห้าม, DNS server, suffix, host): overlay เก็บเป็นค่าเดียวคั่นด้วยจุลภาค เพราะ array ที่สั้นกว่าซ้อนบน array
  ที่ยาวกว่าจะเหลือหางของตัวล่าง; list ว่างเก็บเป็น `","` (`WinsOptions.EmptyList`) เพราะค่าว่างคือสิ่งที่ `[]` ใน JSON อ่านได้
- API token ไม่ถูกส่งกลับ (มีแค่ `isSet`) และไม่ถูกเขียนลง log
- **คำอธิบาย** (`Def.Help`) เป็น raw string หลายบรรทัดใน catalogue ส่งไปทั้งก้อน; หน้าเว็บ (`helpLines`) แปลงทีละบรรทัด:
  ขึ้นต้นด้วย `• ` หรือ `1. ` = คอลัมน์เครื่องหมาย + ข้อความ (บรรทัดที่ตัดจึงเยื้องตรงกัน), ขึ้นต้นด้วยช่องว่าง = บรรทัดย่อยของข้อด้านบน,
  บรรทัดว่าง = เว้นย่อหน้า ค่าแบบ bool แสดงเป็นสวิตช์และใช้คำว่า ON / OFF ทุกที่ (`shown()`)
- **`Dashboard:RemoteAccess`** ไม่ใช่ key ของ Kestrel: `Program.cs` เรียก `WinsOptions.DashboardUrlOverride` หลังแทรก overlay
  ถ้าสวิตช์ขัดกับ host ใน `Kestrel:Endpoints:Management:Url` จะเพิ่ม in-memory source ท้ายสุดที่เปลี่ยน**เฉพาะ host**
  (`0.0.0.0` / `127.0.0.1`) - port ยังเป็นของ `appsettings.json` ที่ installer ดูแล (เลือก port ว่าง, shortcut) จึงไม่มีทางตั้ง port ชนจากหน้าเว็บ

### 6.3 Restart จาก dashboard (`ServiceControl`)

- **Linux (systemd)**: ไม่มี helper - process หยุดตามปกติ (`IHostApplicationLifetime.StopApplication`) ด้วย `Environment.ExitCode = 75`
  (`Program.cs` คืนค่านี้) แล้ว unit (`Restart=on-failure`, `SuccessExitStatus=75`, `RestartForceExitStatus=75`) start ใหม่
- **Windows** ทำตามด้านล่าง

- Service restart ตัวเองไม่ได้ (หยุดแล้วไม่มีใครสั่ง start) จึงเปิด process ที่สอง `WinsAlt.exe --restart-service` ซึ่งอยู่ต่อหลัง service หยุด:
  รอ 0.7 วินาทีให้คำตอบ HTTP ออกไปก่อน → `ControlService(STOP)` → รอ `STOPPED` (สูงสุด 40 วินาที; ถ้าไม่หยุดจะไม่สั่ง start) →
  `StartService` (ลองซ้ำ 3 ครั้ง) → เขียน `service-restart.log` ข้าง exe
- เป็นการ stop ปกติผ่าน SCM ไม่ใช่การ kill: host ปิดตามลำดับและฐานข้อมูลถูกบันทึก; failure action ของ service (restart หลัง 60 วินาที) ไม่เกี่ยว
- P/Invoke ไป advapi32 ใช้ signature ที่ blittable ทั้งหมด (ชื่อ service ส่งเป็น `IntPtr`) เหมือน `NetGetJoinInformation` - ไม่มี marshalling stub
- `ServiceControl.CanRestart` = `WindowsServiceHelpers.IsWindowsService()`; รันจาก console ปุ่มไม่แสดงและ API ตอบ 400
- หน้าเว็บรู้ว่า restart เสร็จจาก `startedAt` ใน `/api/info` ที่เปลี่ยนไป (ไม่เดาจากเวลา) แล้วโหลด `/api/me` ใหม่ เพราะ session อยู่ใน memory

### 6.2 Firewall (`FirewallService`)

- ตรวจว่าเครื่องใช้อะไร (Windows ครั้งเดียว, Linux ตรวจใหม่ทุก 10 วินาที - `ufw enable` ทีหลังต้องเห็นโดยไม่ restart): Windows = netsh; Linux = `firewall-cmd --state` ผ่าน → firewalld, ไม่งั้น `ufw status` เป็น active → ufw,
  ไม่มีทั้งสอง = `none` (หน้าเว็บแจ้งว่าไม่มี firewall ที่บล็อก port)
- firewalld: `--query-port` / `--add-port` / `--remove-port` ใน default zone ทั้ง runtime และ `--permanent`, ตัดสินจาก exit code
- ufw: `ufw allow <port>/<proto> comment "<ชื่อ rule>"` / `ufw delete allow ...`; สถานะอ่านจาก `ufw status` (รันด้วย `LC_ALL=C`)
- บน Linux ใช้ `ProcessStartInfo.ArgumentList` (ไม่มี shell); netsh ใช้สตริงเดียวเพราะ syntax `name="..."` ของมันเอง

Windows:

- Rule ขาเข้าสามตัว: `WinsAlt dashboard` (TCP port ของ dashboard), `WinsAlt NBNS (UDP 137)`, `WinsAlt replication (TCP 8138)`
 - สองตัวหลังใช้ชื่อเดียวกับที่ installer สร้าง; ทุกตัวผูกกับ `program=` ของ exe นี้ และ `profile=any`
- ใช้ `netsh advfirewall firewall show/add/delete rule` (มีตั้งแต่ Windows 7; COM API ของ firewall ใช้กับ Native AOT ไม่ได้)
  สถานะดูจาก exit code ของ `show rule name=` เท่านั้น ไม่ parse ข้อความ (netsh แปลตามภาษาของ Windows)
- Allow = ลบแล้วเพิ่มใหม่เสมอ เพื่อให้ rule ตรงกับ port ที่ใช้อยู่ตอนนี้; ค่าจาก request ไม่ถูกนำไปประกอบ command line
  (`id` ใช้เลือกจากรายการคงที่เท่านั้น); ผลของ `ListAsync` ถูก cache 10 วินาที และหน้าเว็บถามครั้งเดียวต่อการเข้าแท็บ

## 7. Native AOT

- `PublishAot`, `InvariantGlobalization`, `JsonSerializerIsReflectionEnabledByDefault=false`
- เปิด trim/AOT/single-file analyzer + `EnableRequestDelegateGenerator` ตั้งแต่ dev build และยก IL2xxx/IL3xxx เป็น error
- Config อ่านทีละ key (`WinsOptions.FromConfiguration`) ไม่ใช้ `Bind()`/`Get<T>()`
- ไม่มี reflection, `dynamic`, `Activator`, expression tree ในโค้ดของโปรเจกต์
- ผลจริง: `build-aot.ps1` publish ผ่านโดยไม่มี IL warning และ `tools/nbns-smoke.ps1` ผ่านทุกข้อบน native exe

## 7.1 มาตรการความปลอดภัย

| ภัย | มาตรการ | ที่อยู่ในโค้ด |
|---|---|---|
| Packet ผิดรูป / อ่านเกิน buffer | parse ผ่าน `ref struct` ที่ตรวจขอบทุกครั้ง, pointer ย้อนหลังครั้งเดียว, ทิ้ง packet > 576 byte | `NbnsPacketReader`, `NbnsName`, `NbnsServer.ReceiveLoopAsync` |
| UDP flood / reflection | token bucket ต่อ IP ต้นทางใน receive loop (ตารางคงที่ ไม่ lock ไม่ allocate), คิว bounded ทิ้งเมื่อเต็ม | `SourceRateLimiter`, `NbnsServer` |
| ลงทะเบียนชื่อปลอม (poisoning) | โหมด/subnet/ตรงกับ IP ต้นทาง, ชื่อต้องห้าม, static ชนะเสมอ, challenge เจ้าของเดิม (รวมเจ้าของที่อยู่ site อื่น) | `NbnsRequestHandler.CheckPolicy`, `ReplicaStore.TryGetForeignOwner` |
| ฐานข้อมูลโตจน memory หมด | เพดานรวมและต่อ IP นับใต้ write lock | `NameStore.OverQuota_NoLock` |
| ใช้ server เป็นตัวยิงต่อ | จำกัด challenge พร้อมกัน, transaction id สุ่มด้วย CSPRNG | `NameChallengeService`, `PartnerService` |
| Log / Event Log flood | จำกัดอัตราบรรทัด log ต่อ packet | `LogGate` |
| CSRF, DNS rebinding, clickjacking, XSS | custom header บังคับ, ตรวจ Host, CSP + `X-Frame-Options`, Vue escape ทุกค่า (ไม่มี `v-html`) | `ApiEndpoints.UseWinsSecurity`, `index.html` |
| เดารหัสผ่าน / ขโมย session | PBKDF2 210,000 รอบ, ล็อก IP, cookie `HttpOnly` + `SameSite=Strict`, token สุ่ม 256 bit | `AuthService` |
| Path traversal / injection | ค่าจาก URL ไม่เคยถูกใช้เป็น path; GUID ของ adapter ผ่าน `Guid.TryParse` + ต้องอยู่ในรายการก่อนประกอบคำสั่ง | `ApiEndpoints`, `NetbtAdapterService` |
| Cache poisoning (partner / DNS) | id สุ่ม CSPRNG + port ต้นทางใหม่ทุก lookup + ตรวจ IP ผู้ตอบและชื่อ, DNS ต้อง echo คำถาม, ทิ้ง IP ที่ไม่ใช่ host, ชื่อต้องห้ามไม่ถูกถามต่อ, ปุ่มล้าง cache | `PartnerService.LookupAsync`, `DnsFallbackResolver`, `Ipv4.IsUsableHost` |
| Replication ถูกสอดแนม/ปลอม | รับเฉพาะ IP ของ peer, HMAC สองทิศ + nonce, จำกัดเวลาก่อนยืนยันตัวตน | `ReplicationWorkers`, `ReplicationWire` |

สิ่งที่มาตรการเหล่านี้**ไม่ได้**แก้: IP ต้นทางของ UDP ปลอมได้บน LAN เดียวกัน และ NBNS ไม่มีการเข้ารหัสหรือลายเซ็น

## 8. เชื่อมกับ name server อื่น

ลำดับการตอบ client (static มาก่อนเสมอ): **local static → replica static → local registration → replica registration → partner → DNS**

### 8.1 Replication ระหว่าง WinsAlt (`ReplicationService`, `ReplicaStore`, `ReplicationWorkers.cs`)

- แต่ละเครื่อง**เป็นเจ้าของ**ชื่อที่ลงทะเบียนกับมัน (registration ของ client, static mapping, ชื่อตัวเอง) และเป็นเครื่องเดียวที่แก้ชื่อนั้นได้
- แต่ละเครื่อง **pull snapshot ทั้งชุด** ของชื่อที่ peer เป็นเจ้าของทุก 30 วินาที แล้วเก็บเป็นสำเนาอ่านอย่างเดียวใน `ReplicaStore`
  (dictionary immutable ที่สลับทั้งก้อน - query อ่านได้โดยไม่ lock ไม่ allocate)
- ใช้ snapshot ทั้งชุดจึงไม่มี delta, version vector หรือ tombstone: ชื่อที่ถูกปล่อยหรือหมดอายุที่ต้นทางแค่ไม่อยู่ใน snapshot ถัดไป
- Snapshot มีเฉพาะชื่อที่ peer เป็นเจ้าของ ไม่รวมสำเนา → ไม่วนลูป แต่ต้องตั้งแบบ **full mesh**
- ตอบ query: ชื่อ local ชนะสำเนา; ชื่อซ้ำจากหลาย peer: group รวมสมาชิก, อย่างอื่นเลือก static ก่อน แล้วตัวที่ refresh ล่าสุด
- **Replication ไม่เคยลบหรือตัด IP ของ record local** - 1.7.0-1.7.2 ลอง "ตัวที่ใหม่กว่าชนะ" (ระดับชื่อ แล้วระดับ IP) จากเวลาต่ออายุ
  ของ peer และถูกถอน: client ต่ออายุกับทั้งสอง server (WINS หลัก + สำรอง) และบาง host ลงทะเบียนคนละการ์ดกับคนละ server
  เวลาของ peer จึงไม่ใช่หลักฐานว่า IP ของเราตาย; ทางที่ถูกถ้าจะทำ = challenge IP นั้นโดยตรง (ข้อเสนอใน TODO)
- Protocol: TCP (ค่าเริ่มต้น 8138) แยกจากพอร์ต dashboard - server ส่ง nonce 16 byte, แต่ละ frame คือ
  `[int32 length][JSON][HMAC-SHA256(key, nonce ‖ direction ‖ body)]` ยืนยันตัวตนทั้งสองทิศ; request ที่ยืนยันไม่ผ่านไม่ได้คำตอบ
  ข้อมูลไม่ได้เข้ารหัส; exchange เป็นแบบอ่านอย่างเดียว peer แก้ฐานข้อมูลของเครื่องนี้ไม่ได้
- สำเนาไม่ได้บันทึกลงดิสก์ - หลัง restart ดึงใหม่ทันที

### 8.2 Query forwarding ไปยัง WINS อื่น (`PartnerService`)

- ชื่อที่ไม่พบจะถูกส่งเป็น name query ธรรมดา (unicast UDP 137, RD=1) ไปยัง partner ทุกตัวพร้อมกันจาก socket แยก
  คำตอบแรกที่เจอถูกส่งต่อให้ client; ใช้ได้กับ NBNS server ใดก็ได้เพราะใช้แค่ protocol ฝั่ง client
- ทางเดียว: ชื่อที่ลงทะเบียนที่นี่ไม่ถูกคัดลอกไปให้ partner
- Cache: เจอ 5 นาที, ไม่เจอ 1 นาที; lookup เดียวกันที่กำลังวิ่งอยู่ถูกรวมกัน
- Query ที่มาจาก partner เองไม่ถูกส่งต่อกลับ (กันลูป)
- Partner ที่เงียบ 3 ครั้งติดจะไม่ถูกรอคำตอบ จนกว่าจะถึงรอบ probe (30 วินาที) หรือกลับมาตอบ
- คำตอบของ partner มีแค่ชื่อ, IP, ธง group/node type และ TTL - ไม่มี state / owner / version ของระเบียน

### 8.3 NetBT ของ Windows (`NetbtAdapterService`)

- อ่านสถานะจาก registry `NetBT\Parameters\Interfaces\Tcpip_{GUID}\NetbiosOptions` (0 default, 1 เปิด, 2 ปิด)
- สั่งเปลี่ยนด้วย `Win32_NetworkAdapterConfiguration.SetTcpipNetbios` ผ่าน `powershell.exe` + `Get-WmiObject`
  (System.Management ใช้กับ Native AOT ไม่ได้; CIM cmdlets ไม่มีบน PowerShell 2.0 ของ Windows 7)
