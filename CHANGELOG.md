# Changelog

บันทึกงานที่เสร็จแล้วของ WinsAlt เรียงจากใหม่ไปเก่า แผนงานต่อไปอยู่ใน [docs/ROADMAP.md](docs/ROADMAP.md)

เวอร์ชัน 1.0.0-1.1.4 ออกในวันที่ 2026-10-05, ตั้งแต่ 1.2.0 ออกในวันที่ 2026-10-06 ทุกเวอร์ชันผ่าน `tools\nbns-smoke.ps1`
บน Native AOT exe; ตั้งแต่ 1.1.0 ผ่าน `tools\replication-smoke.ps1` และตั้งแต่ 1.3.0 ผ่าน `tools\security-smoke.ps1` ด้วย

## 1.8.2 (2026-10-08)

- **ชื่อ server ใน tab ของ browser**: title เป็น `<ชื่อ server> - WinsAlt` (เช่น `WINS-LINUX - WinsAlt`) ชื่อขึ้นก่อนเพื่อให้แยก tab ได้แม้ tab แคบ
  และแสดงชื่อ server ใต้คำว่า WinsAlt ที่มุมซ้ายบนของหน้า; `/api/info` (เปิดโดยไม่ต้องเข้าระบบ) ส่ง `server` เพิ่ม = ชื่อเดียวกับที่ประกาศผ่าน
  NetBIOS (ตัดส่วน domain ออก) จึงไม่ได้เปิดเผยอะไรเพิ่ม; `security-smoke` ตรวจค่านี้
- ถอนผ่านแพ็กเกจ: ข้อความท้ายบอกวิธีลบข้อมูลที่ถูกต้อง (`apt purge winsalt` หรือ `rm -rf /var/lib/winsalt`) แทน `--purge` ของ uninstall.sh

## 1.8.1 (2026-10-08)

**ไอคอนโปรแกรม, แพ็กเกจ .deb / .rpm, คู่มือติดตั้ง, ไฟล์สำหรับ GitHub release**

- **ไอคอน**: `Assets/winsalt.ico` (16 ถึง 256 px, รูปเดียวกับ favicon ของ dashboard) ฝังใน `WinsAlt.exe` (`ApplicationIcon`, ใช้ได้กับ
  Native AOT), เป็นไอคอนของ shortcut บน desktop (เดิม shortcut ไม่มีรูปเพราะ exe ไม่มีไอคอน), ของตัวติดตั้งและรายการใน Apps and features
- **แพ็กเกจ Linux**: `winsalt_<ver>-1_amd64.deb` และ `winsalt-<ver>-1.x86_64.rpm` สร้างด้วย nfpm ใน `build-linux.sh`
  (nfpm เป็น binary ตัวเดียว ดาวน์โหลดพร้อมตรวจ SHA-256 ไว้ใน `/root/winsalt/tools` ของ WSL ไม่ติดตั้งอะไรลงระบบ)
  - แพ็กเกจแตกไฟล์ชุดเดียวกับ `.tar.gz` ไว้ที่ `/usr/lib/winsalt` แล้วเรียก `install.sh --packaged` ผลจึงเหมือนติดตั้งเองทุกอย่าง
    (`/opt/winsalt`, `/var/lib/winsalt`, unit ใน `/etc/systemd/system`) และอัปเกรดข้ามแบบ (tar.gz เป็น rpm/deb) ได้
  - `--packaged`: ไม่เรียก apt-get / dnf (package manager กำลังทำงานอยู่ ใช้ dependency ของแพ็กเกจแทน: deb `libssl3t64 | libssl3,
    iproute2, curl, systemd`, rpm `openssl-libs, iproute, curl, systemd`), distro ที่ไม่อยู่ในรายการเป็นคำเตือนแทนการหยุด, เรียก `ldconfig` ก่อนตรวจ libssl
  - ถอน: `remove` (deb) / `dnf remove` เรียก `uninstall.sh` (เก็บข้อมูล), `apt purge` ลบ `/var/lib/winsalt` ด้วย; การอัปเกรดไม่แตะ service
- **คู่มือติดตั้ง** `INSTALL.md` / `INSTALL.th.md`: ดาวน์โหลด, ตรวจ checksum, ติดตั้งทั้ง 4 แบบ, เข้าระบบครั้งแรก, ปิด NetBT, DHCP 044/046,
  อัปเกรด, ถอน, แก้ปัญหา; มาในตัวติดตั้ง Windows, `.tar.gz` และ `/usr/share/doc/winsalt`; README ทั้งสองภาษาชี้ไปหน้า Releases และคู่มือ
- `tools/release-files.ps1`: รวมไฟล์ 4 ตัวของเวอร์ชันไว้ที่ `Outputelease-<ver>\` พร้อม `SHA256SUMS.txt` (รูปแบบ `sha256sum -c`)
- ไม่มีการเปลี่ยนโค้ดของโปรแกรม (Linux binary ทำงานเหมือน 1.8.0)

## 1.8.0 (2026-10-07)

**หลายภาษา (TH / EN), License MIT, หน้ายอมรับใน installer, ตัดอักขระพิเศษ (em dash, จุดกลาง, ellipsis) ออก**

- **Dashboard หลายภาษา**: ข้อความทั้งหมดของหน้าเว็บมาจากไฟล์ภาษา `wwwroot/lang/en.json` และ `th.json` (ฝังในโปรแกรม)
  - เลือกภาษาครั้งแรกตามภาษาของ browser (ไทยเห็นไทย, อื่นๆ เห็นอังกฤษ) เปลี่ยนได้ที่มุมล่างขวา และจำไว้ใน browser
  - เพิ่มภาษาเองได้โดยไม่ต้อง build ใหม่: วาง `<code>.json` ในโฟลเดอร์ `lang` ของโฟลเดอร์ข้อมูล (Windows: ข้าง exe, Linux:
    `/var/lib/winsalt/lang`) ไฟล์ที่ใช้ code เดียวกับภาษาที่มากับโปรแกรมจะใช้แทน (แก้คำแปลได้) คำที่ไฟล์ไม่มีใช้ภาษาอังกฤษแทน
  - API ใหม่ (เปิดโดยไม่ต้องเข้าสู่ระบบ เพราะหน้าเข้าสู่ระบบต้องแปลด้วย): `GET /api/lang`, `GET /api/lang/{code}`
    (code ตรวจด้วย `^[a-z]{2,3}(-[a-z0-9]{2,8})?$` ก่อนใช้เป็นชื่อไฟล์, ไฟล์เกิน 256 KB หรือไม่ใช่ JSON object ถูกข้าม)
  - คำอธิบาย Settings ทั้ง 35 รายการย้ายจาก C# ไปอยู่ในไฟล์ภาษา (มีภาษาอังกฤษครบแล้ว) และแก้ส่วนที่เขียนถึงแต่ Windows
    ให้ครอบคลุม Linux (Listen address, ชื่อเครื่อง, workgroup, DNS); `/api/settings` ไม่ส่ง `help` แล้ว และ `pendingRestart` ส่งเป็น key
  - ข้อความ error จาก server แปลได้ผ่าน key `server.<ข้อความอังกฤษ>`; ข้อความใน Events (log) ยังเป็นภาษาอังกฤษ
  - วันที่แสดงแบบตัวเลข (วัน/เดือน/ปี ค.ศ.) ทุกภาษา
- **License MIT**: `LICENSE`, `THIRD-PARTY-NOTICES.md` (Vue.js MIT, JetBrains Mono OFL 1.1, Feather icons MIT, .NET MIT)
  และหน้า "เกี่ยวกับและสัญญาอนุญาต" ที่ footer ของ dashboard
- **Installer**: เลือกภาษาไทย / อังกฤษตอนเริ่ม, หน้า License (ยอมรับ / ไม่ยอมรับ), หน้า "ขอบเขตและข้อจำกัดการใช้งาน"
  (`installer/usage-terms.en.txt` / `.th.txt`, ต้องเลือกยอมรับจึงกดถัดไปได้), ข้อความตอนจบเป็นภาษาที่เลือก;
  ติดตั้ง LICENSE.txt, THIRD-PARTY-NOTICES.md และไฟล์ขอบเขตการใช้งานไว้ใน `{app}`; แพ็กเกจ Linux มีไฟล์ชุดเดียวกัน
- **ตัดอักขระที่ผู้ใช้ไม่ต้องการ** (em dash, en dash, จุดกลาง, ellipsis, อัญประกาศโค้ง) ออกจากหน้าเว็บ ข้อความ log/error เอกสาร
  comment และสคริปต์ทั้งหมด
- ทดสอบ: ทั้งสามชุดบน Windows (Native AOT) และ CentOS Stream 10 (security เพิ่มข้อตรวจภาษา: ทุก setting มีชื่อและคำอธิบาย
  ทั้ง en และ th, ไฟล์ภาษาที่วางเพิ่ม/แทนที่/เสียหาย, code ที่พยายามอ่านไฟล์อื่นได้ 404); Edge ภาษาไทยไล่ทุกแท็บ + คำอธิบาย + หน้าเกี่ยวกับ
  แล้วสลับเป็นอังกฤษ ไม่พบ key ดิบ, `{0}` หรืออักขระต้องห้ามบนจอ; ติดตั้ง 1.8.0 บน CentOS แล้ว (SELinux ไม่มี denial)
- เตรียมขึ้น GitHub: README ภาษาอังกฤษเป็นหน้าหลัก (ฉบับไทยย้ายเป็น `README.th.md`), `CONTRIBUTING.md` (วิธีเพิ่มภาษา),
  `SECURITY.md`, และ `tools/export-public.ps1` ที่สร้างชุดไฟล์สาธารณะโดยตัดเอกสารภายในและแทนชื่อเครื่อง / IP จริง
  (ตรวจซ้ำแล้วล้มถ้ายังเหลือ); โค้ดชุดที่ export build ผ่าน
- comment ที่อ้างถึงโปรเจกต์อื่นของผู้พัฒนาและ IP ตัวอย่างจากเครือข่ายจริงถูกเปลี่ยนเป็นค่ากลาง
- ยังไม่ได้ทดสอบ: หน้าใหม่ของ installer ยังไม่ได้เปิดดูจริง (compile ผ่าน แต่หน้าจอเครื่อง dev ถูกล็อก จับภาพไม่ได้)

## 1.7.4 (2026-10-07)

**ทดสอบบน CentOS Stream 10 จริงครั้งแรก** (VM 192.0.2.14, glibc 2.39, **SELinux Enforcing**, firewalld active)

- แก้: hostname ที่ยังเป็น `localhost.localdomain` (ค่าของ install ใหม่) ทำให้ประกาศชื่อตัวเองเป็น `LOCALHOST` (และจะถูก replicate ไปทุก peer)
 - ตอนนี้ไม่ประกาศ และเตือนใน log กับตอนจบ `install.sh` ให้ตั้ง hostname (`hostnamectl set-hostname`) หรือ `Wins:SelfName`
- แก้: workgroup อ่านจาก `/etc/samba/smb.conf` เฉพาะเมื่อมี Samba server (`smbd`) จริง - RHEL / CentOS มีไฟล์นี้ (`workgroup = SAMBA`)
  ติดมากับ samba-common แม้ไม่ได้ติดตั้ง Samba ทำให้ประกาศเป็นสมาชิก workgroup "SAMBA"
- ผลทดสอบบน CentOS Stream 10 (1.7.3 → 1.7.4):
  - `install.sh`: รู้จัก distro; ลบ curl ออก (`rpm -e --nodeps`) แล้ว **ติดตั้งคืนผ่าน dnf** (ทางที่ไม่เคยทดสอบ); firewalld เปิด 3 port ทั้ง runtime และ permanent;
    ติดตั้งทับ (upgrade) ผ่าน
  - **SELinux Enforcing: ไม่มี AVC denial เลย** - service รันเป็น `unconfined_service_t`, ไฟล์ `/opt/winsalt` เป็น `usr_t`; bind UDP 137,
    เขียน `/var/lib/winsalt`, เรียก firewall-cmd, ปุ่ม Restart (systemd กลับมาใน 3 วินาที `Result=success`) ผ่านทั้งหมด
  - **ปุ่ม firewalld ในหน้าเว็บกดจริงครั้งแรก**: Remove UDP 137 → `nbtstat -A` จาก Windows ไม่ตอบ; Allow → ตอบ; runtime / permanent ตรงกัน
  - Windows → `http://192.0.2.14:8137` ได้ 401 จนกว่าจะ sign in; admin / admin → เปลี่ยนรหัส ผ่าน
  - ชุดทดสอบบน VM (pwsh 7.6.6 จาก tarball ที่ `/opt/microsoft/powershell/7`): replication 25, security 95, nbns 30 ผ่านครบ
    (nbns น้อยกว่า Ubuntu 4 ข้อ: ข้อ node status ข้ามเมื่อเครื่องไม่ประกาศชื่อตัวเอง - hostname ยังเป็น localhost)
- Windows: ทั้งสามชุดผ่านบน Native AOT
- WINS-LINUX: ผู้ใช้ตั้ง partner forwarding แล้ว - `CLIENT-01<20>` ตอบ 192.0.2.137 ตรงกับ SERVER-A

## 1.7.3 (2026-10-07)

**Replication กลับไปทำงานแบบ 1.6.0: ไม่ลบหรือตัด IP ของชื่อที่ลงทะเบียนกับเครื่องนี้เลย ไม่ว่า peer จะถืออะไร**

- เหตุผล (สิ่งที่เรียนรู้จากเครือข่ายจริง): client ต่ออายุชื่อเดียวกันกับ**ทั้งสอง server** (WINS หลัก + สำรอง) และบางเครื่อง
  ลงทะเบียนคนละการ์ดกับคนละ server (เช่น CLIENT-04: LAN 192.0.2.114 + VPN 10.0.0.6) - เวลาต่ออายุของ peer
  จึงบอกไม่ได้ว่า IP ของเครื่องนี้ "เลิกใช้แล้ว" หรือ "ยังใช้แต่ลงทะเบียนที่อื่น" ทั้งระดับชื่อ (1.7.0, 1.7.1) และระดับ IP (1.7.2)
- IP เก่าที่ค้าง (client ย้าย VLAN) หมดอายุเอง (~3.5 วันตาม TTL ที่ client ขอ) หรือลบเองในหน้า Registered names  - 
  ระหว่างนั้นคำตอบมาจากสำเนาของ server อีกตัว
- `install.sh`: แก้ `cmd | grep -q` ภายใต้ `set -o pipefail` - grep -q ออกทันทีที่เจอ ทำให้ cmd ตายด้วย SIGPIPE และ test ล้มทั้งที่เจอ
  (บน Ubuntu 26.04 ขึ้น "Installing what is missing: libssl" ทั้งที่มีอยู่; เครื่องไม่มี internet จะติดตั้งไม่ผ่าน) - เปลี่ยนเป็น `> /dev/null` ทุกจุด
- ทดสอบ: `replication-smoke` มีข้อ "replication never removes a local record or address" ด้วย record ที่ทำให้ 1.7.1 และ 1.7.2 พัง
  (ทั้งสองรุ่นจะตกข้อนี้); ผ่านทั้งสามชุดบน Windows (Native AOT) และ Ubuntu 26.04 (port ทดสอบ); ติดตั้งบน VM แล้ว 5 นาทีไม่มี warning;
  `install.sh` 3 รอบไม่ขึ้น libssl ผิดอีก

## 1.7.2 (2026-10-07) - ถอนแล้ว

ลบ IP ทีละตัว: IP ของเครื่องนี้ที่ peer ไม่มี ถูกลบถ้า IP ล่าสุดของ peer ต่ออายุทีหลัง - รันบน VM 5 นาที ลบ IP ที่ยังใช้งาน 4 ครั้ง
(CLIENT-04<00>/<20> 192.0.2.114 เพราะ SERVER-A มี VPN 10.0.0.6; CLIENT-02<00> 192.0.2.135) แล้ว VM ตอบ IP VPN ให้เครื่อง LAN
ถอยกลับเป็น 1.6.0 ทันที ไฟล์ติดตั้งถูกลบ ไม่เคยลง SERVER-A

## 1.7.1 (2026-10-07) - ถอนแล้ว

ลบทั้งชื่อเมื่อชุด IP ต่างกันและ peer ใหม่กว่า - รันบน VM ไม่กี่นาที VM ทิ้ง record ที่ถูกของตัวเอง (CLIENT-05, CLIENT-02, CLIENT-03 ฯลฯ)
แล้วตอบชุด IP ของ SERVER-A ที่มี IP เก่าปน เพราะ client ต่ออายุที่ SERVER-A ด้วย record ฝั่งนั้นจึง "ใหม่กว่า" - ถอยกลับเป็น 1.7.0
ไฟล์ติดตั้งถูกลบ ไม่เคยลง SERVER-A

## 1.7.0 (2026-10-07) - กติกา replication ถูกเอาออกใน 1.7.3

**Replication: ชื่อที่ลงทะเบียนล่าสุดชนะ** (ผู้ใช้ถามกรณีย้าย client 192.0.2.x จาก SERVER-A ไป WINS-LINUX)

- ปัญหาเดิม: client ที่ย้ายไปลงทะเบียนกับ server อื่นไม่ release ชื่อที่ server เดิม → server เดิมตอบ address เก่าของตัวเอง
  (ชื่อ local ชนะสำเนาเสมอ) จนกว่าจะหมดอายุ (~3.5 วันตาม TTL ที่ client ขอ) แม้ address ที่ server ใหม่จะเปลี่ยนไปแล้ว
- ตอนนี้: ทุกครั้งที่ดึง snapshot จาก peer ถ้า peer มีชื่อ unique / multihomed เดียวกันที่ **ลงทะเบียนหรือ refresh ใหม่กว่า** และ
  **address ไม่ซ้ำกัน** เครื่องนี้ทิ้ง registration ของตัวเองแล้วตอบจากสำเนาแทน และบันทึก log
  "`NAME<20> moved to <peer>: <ใหม่> (was <เก่า> here)`" (แท็บ Events)
  - เทียบ "ใหม่กว่า" ด้วยเวลา refresh ล่าสุด (`refreshedAt` ใหม่ ใน snapshot และ wins-db.json); ถ้าฝั่งใดไม่รู้ (record จาก wins-db.json
    ก่อน 1.7.0 หรือ peer รุ่น 1.6.0) เทียบเวลาหมดอายุแทน - client เดียวกันขอ TTL เท่ากันทุก server เวลาหมดอายุที่ช้ากว่าจึงคือ refresh ที่ใหม่กว่า
  - ไม่แตะ: static mapping (ยังชนะเสมอ), group (WORKGROUP ฯลฯ), ชื่อที่ address ตรงกัน (เครื่องเดียวกัน), เท่ากัน = เก็บของตัวเอง
  - ทำตอนรับ snapshot ไม่ใช่ตอนตอบ query → hot path ไม่เปลี่ยน; ลำดับการตอบยังเหมือนเดิม
  - ไม่ ping-pong: ถ้าเครื่องเก่ากลับมาลงทะเบียนที่ server เดิม จะถูก challenge กับเจ้าของปัจจุบันก่อน (เหมือนเดิม)
  - 1.6.0 ↔ 1.7.0 replicate กันได้ (field ใหม่ถูกข้ามโดยรุ่นเก่า) แต่**เครื่องที่ถือ record เก่าต้องเป็น 1.7.0** จึงจะทิ้งมัน
  - ใช้นาฬิกาของแต่ละ server - server ควร sync เวลา (NTP / domain time)
- ทดสอบ: `replication-smoke` เพิ่ม client ย้ายพร้อมเปลี่ยน address, record จาก wins-db.json รุ่นเก่า (เทียบด้วยเวลาหมดอายุ),
  เครื่องเดียวกันที่ address ตรงกันไม่ถูกทิ้ง - ผ่านทั้ง Windows (Native AOT) และ Ubuntu 26.04 (port ทดสอบ); `security-smoke`, `nbns-smoke` ผ่านทั้งสองที่

## 1.6.0 (2026-10-07)

**Ubuntu 26.04 (เพิ่มเติมหลังออก, ไม่มีการแก้โค้ด)**: ผู้ใช้ upgrade VM `wins-linux` จาก 24.04 เป็น 26.04.1 (glibc 2.43, kernel 7.0)
- service 1.6.0 รันต่อเองหลัง upgrade; `libssl3t64` ยังเป็นชื่อ package ของ OpenSSL บน 26.04; ufw rule ยังอยู่ครบ
- `install.sh` (แบบติดตั้งทับ) ผ่าน: รู้จัก 26.04, `ss` ของ 26.04 บอกชื่อโปรแกรมที่ถือ port ได้, ufw, listener check, เก็บ 71 ชื่อที่ลงทะเบียนไว้ครบ;
  distro ปลอม (Debian 12) ถูกหยุด; ปุ่ม Restart (systemd 258) กลับมาใน 3 วินาที `Result=success`
- ชุดทดสอบทั้งสามชุดผ่าน (security 95, replication 20, nbns 34); `nbtstat -A` จาก Windows; Edge sign in + หน้า Settings / ตาราง ufw
- **Replication Windows ↔ Linux ครั้งแรกกับเครื่องจริง** (ผู้ใช้ตั้งเอง): VM ดึงจาก `SERVER-A` 490 ชื่อ; client จริงจาก 192.0.2.x
  ลงทะเบียน / refresh ตรงมาที่ VM; ถามชื่อเดียวกันกับทั้งสองเครื่อง (CLIENT-06, CLIENT-03, CLIENT-07 ฯลฯ) ได้คำตอบตรงกัน  - 
  ต่างกันเฉพาะชื่อที่ SERVER-A ได้มาจาก partner WINS (เช่น `CLIENT-01<20>`) เพราะ VM ไม่ได้ตั้ง partner
- ติดตั้งใหม่แบบล้างข้อมูลบน 26.04 (ผู้ใช้อนุญาต): `uninstall.sh --purge` ลบ rule ufw ครบ → 8137 ไม่ว่าง → ใช้ 8139 →
  purge อีกรอบ → ติดตั้งสะอาด: Windows ได้ 401 จนกว่าจะ sign in, admin / admin → หน้าต่างเปลี่ยนรหัส → กล่องแดง → เปลี่ยนรหัส → กล่องหาย;
  UDP 137 ไม่ว่าง → หยุด; คืน `replication.json` (peer SERVER-A + key) จาก backup → replication กลับมา 490 ชื่อ และ client จริง
  ลงทะเบียนกลับเข้ามา 60 ชื่อภายใน 40 วินาที
- ทิศ Linux → Windows: หน้า Registered names ของ SERVER-A (1.6.0, ภาพจากผู้ใช้) มี `WINS-LINUX<00>/<20>` จาก replication  - 
  แสดงแค่ 2 ชื่อเพราะรายการแสดงแถวเดียวต่อชื่อ (แถวที่ client จะได้คำตอบ) replica ถูกซ่อนเมื่อ SERVER-A มีชื่อเดียวกันเป็นของตัวเอง
  และ client 192.0.2.x ยังมี registration เดิมที่ SERVER-A (TTL 6 วัน)

ค่าเริ่มต้นใหม่ของการติดตั้งใหม่ + `install.sh` ตรวจเครื่องก่อนติดตั้ง

- **dashboard เปิดให้ LAN ตั้งแต่ติดตั้ง**: `appsettings.json` bind `http://0.0.0.0:8137` (เดิม `127.0.0.1`)
  - installer ของ Windows เพิ่ม firewall rule "WinsAlt dashboard" ใน task firewall เดิม (เฉพาะเมื่อ dashboard เปิดออก LAN)
    และหน้าจบบอก `http://<ชื่อเครื่อง>:<port>` + admin / admin
  - การติดตั้งทับ (upgrade) ทั้ง Windows และ Linux เก็บ `appsettings.json` เดิม → เครื่องที่ใช้อยู่ไม่เปลี่ยน
- **ต้อง sign in ก่อนเห็นข้อมูล**: `Dashboard:RequireSignInToView` เป็น true โดยค่าเริ่มต้น
  และ**ไม่ยกเว้นเครื่อง server เอง (loopback) อีกต่อไป** - ใช้กับเครื่องที่อัปเกรดมาและเปิดตัวเลือกนี้ไว้ด้วย
  (reverse proxy บนเครื่องเดียวกันจะทำให้ทุกคนดูเหมือนมาจาก loopback)
- **รหัสเริ่มต้น admin / admin** แทน setup mode (เดิมต้องตั้งรหัสแรกจากเครื่อง server เอง)
  - ไม่มี `auth.json` = ใช้ admin / admin; เปลี่ยนรหัสแล้วจึงเขียน `auth.json`
  - sign in ด้วยรหัสเริ่มต้น → หน้าต่างเปลี่ยนรหัสเปิดเองทันที (ช่องรหัสเดิมกรอกให้) และกล่องเตือนสีแดงค้างที่หัวหน้าจนกว่าจะเปลี่ยน
  - `/api/me` บอก `defaultPassword` เฉพาะกับ admin - คนที่ยังไม่ sign in ไม่รู้ว่าเครื่องนี้ยังใช้รหัสเริ่มต้น
  - `auth.json` ที่อ่านไม่ได้ = ไม่มีใคร sign in ได้ (ไม่ย้อนไปใช้ admin / admin เอง) แก้ด้วยการลบไฟล์
  - เอา `canSetPassword` / `passwordSet` ออกจาก `/api/me` และปุ่ม *Set password* ออกจากหน้าเว็บ
- **`install.sh` (Linux)**
  - ตรวจ distro: Debian 13, Ubuntu 24.04 / 26.04, CentOS Stream 10 (อย่างอื่นต้อง `--force`) และ x86_64
  - ติดตั้งสิ่งที่ขาดด้วย apt / dnf: OpenSSL 3 (`libssl3t64` / `openssl-libs` - .NET ใช้ hash รหัสผ่าน), iproute2 (`ss`), curl;
    ตรวจ `ldd` ของ binary
  - ตรวจ port: UDP 137 / TCP 8138 ไม่ว่าง = หยุดพร้อมบอกชื่อโปรแกรม; TCP 8137 ไม่ว่าง = ใช้ 8139 / 18137 / 9137 แทน
  - ถ้า ufw / firewalld เปิดอยู่แล้ว เปิด 137/udp, 8138/tcp และ port dashboard ให้ (comment ตรงกับตาราง Firewall); `--no-firewall` ข้าม
  - ข้อความจบบอก URL ด้วย IP ของเครื่อง และ admin / admin
  - `uninstall.sh` ลบ rule ของ ufw ที่มี comment "WinsAlt ..."
  - หลัง start ถาม service ว่า UDP 137 ขึ้นจริงไหม (`winsalt --listener-state`) แล้วบอกผล เหมือน installer ของ Windows
- `/api/info` (เปิดโดยไม่ต้อง sign in) บอกสถานะ listener (`listener: "Active"` ฯลฯ) - ขั้นสุดท้ายของ installer (`--listener-state`)
  เดิมอ่าน `/api/status` ซึ่งตอนนี้ต้อง sign in จะได้ 401 ตลอดและแจ้ง "did not answer yet" ทุกครั้ง; มีชุดทดสอบตรวจแล้ว
- คำอธิบายในหน้า Settings ของ *Open the dashboard to other computers* และ *Sign-in required to view* เขียนใหม่ตามค่าเริ่มต้นใหม่
- ทดสอบ
  - Windows (Native AOT): `security-smoke` (เพิ่ม admin / admin, เปลี่ยนรหัส, auth.json เสีย, ไม่ยกเว้น loopback, appsettings ที่แจกไป),
    `replication-smoke`, `nbns-smoke` 36/36; Edge: sign in admin / admin → หน้าต่างเปลี่ยนรหัสเปิดเอง → ปิดแล้วกล่องแดงยังอยู่ →
    เปลี่ยนรหัส → sign in ใหม่ → กล่องหาย; ก่อน sign in ไม่มีคำว่า admin / admin บนจอ
  - Ubuntu 24.04 VM: ทั้งสามชุดผ่าน (security 95, nbns 34); `install.sh` บน distro ปลอม (Debian 12) → หยุด, UDP 137 ไม่ว่าง → หยุด,
    8137 ไม่ว่าง → ใช้ 8139 และเปิด ufw ให้ 8139; ติดตั้งสะอาด → Windows เปิด `http://192.0.2.13:8137` ได้ทันที, ข้อมูล 401
    จนกว่าจะ sign in, ทำ flow เปลี่ยนรหัสใน Edge ผ่าน; ติดตั้งทับ → รหัสใหม่ยังใช้ได้, admin / admin ใช้ไม่ได้; `uninstall.sh --purge` ลบ rule ufw ครบ
  - ยังไม่ได้ทดสอบ: ตัว installer ของ Windows (compile ผ่าน แต่ไม่ได้รันบนเครื่องจริง - รัน installer ในเครื่อง dev ไม่ได้);
    การติดตั้ง package ที่ขาด (เครื่อง Ubuntu มีครบอยู่แล้ว); firewalld

## 1.5.4 (2026-10-07)

ทดสอบบน **Ubuntu 24.04.5 จริง** (VM `wins-linux` 192.0.2.13, glibc 2.39, ufw) ครั้งแรก

- แก้: ตาราง Firewall ในหน้า Settings จำชนิด firewall ไว้ตลอดอายุ process - ถ้าเปิดหน้า Settings ก่อนแล้วค่อย `ufw enable`
  ตารางยังบอก "none" จนกว่าจะ restart service (เจอจริงบน Ubuntu) ตอนนี้ตรวจใหม่ทุก 10 วินาทีเหมือนรายการ rule
  (Windows ไม่เปลี่ยน)
- `install.sh`: ถ้ายังไม่มีรหัสผ่าน admin จะพิมพ์วิธีตั้งรหัสผ่านแรกบน server ที่ไม่มีหน้าจอ (SSH tunnel หรือ curl)  - 
  ข้อความเดิม "เปิดบนเครื่องนี้" ทำไม่ได้บน Linux server; README อธิบายเพิ่ม
- ผลทดสอบบน Ubuntu 24.04:
  - binary ใช้ glibc สูงสุด 2.34 (build บน Debian 13) - รันบน 2.39 ได้
  - `install.sh` ครั้งแรกและแบบอัปเกรด (เก็บ settings, รหัสผ่าน, ข้อมูลไว้), service `active`, ถือ UDP 137
  - `nbtstat -A 192.0.2.13` จาก Windows จริงเห็นชื่อ `WINS-LINUX <00>/<20>`; `nbns-smoke` จาก Windows ข้ามเครือข่ายมาที่ UDP 137
    ผ่าน 17/17 และบน VM เอง (instance ทดสอบ) 34/34; `security-smoke` และ `replication-smoke` บน VM ผ่านครบ
  - ตั้งรหัสผ่านแรกด้วย curl, เปิด RemoteAccess, กด Restart ผ่าน API → systemd `Deactivated successfully` → start ใหม่ใน 3 วินาที
  - **ปุ่ม Allow / Remove ของ ufw กดจริงจากหน้าเว็บ (Edge ผ่าน SSH tunnel) ครั้งแรก**: Allow ครบ 3 rule (มี comment, ได้ทั้ง v4/v6),
    Windows เข้า dashboard / `nbtstat` / TCP 8138 ได้; Remove UDP 137 → `nbtstat` เข้าไม่ได้ทั้ง v4/v6; ไม่ติด `ProtectSystem`
  - กล่องแดง "Cannot reach the WinsAlt service" ขึ้นเมื่อ `systemctl stop winsalt` และหายเองเมื่อ start (ยืนยันหลังการแก้ 1.5.3)
- Windows: `nbns-smoke`, `security-smoke` และ `replication-smoke` ผ่านบน Native AOT exe

## 1.5.3 (2026-10-07)

- แก้: เมื่อเปิด **Sign-in required to view** แล้วกดเปลี่ยนแท็บ (หรือ sign out) ขณะยังไม่ sign in หน้าเว็บขึ้นกล่องแดง
  "Cannot reach the WinsAlt service (Sign in to view this server.)" ทั้งที่ service ทำงานปกติ - ตอนนี้คำตอบ 401 ถูกนับเป็น
  "ต้อง sign in" เสมอ ไม่ใช่ "ติดต่อ service ไม่ได้" และระหว่างรอ sign in การเปลี่ยนแท็บไม่เรียกข้อมูลเลย
  กล่องแดงยังขึ้นตามเดิมเมื่อติดต่อ service ไม่ได้จริง
- ทดสอบด้วย Edge จาก IP ของ LAN: ยังไม่ sign in กดครบ 7 แท็บ และ sign out ในแต่ละแท็บ - ไม่มีกล่องแดง และไม่มี request ที่ได้ 401

## 1.5.2 (2026-10-07)

- แก้: เมื่อเปิด **Sign-in required to view** แล้ว sign out ขณะอยู่หน้า **Settings** (และ Registered names, Static mappings,
  Partner servers, Query log, Events) ข้อมูลที่โหลดไว้ตอน sign in ยังค้างบนจอ - ตอนนี้ทุกหน้าถูกซ่อนและข้อมูลที่โหลดไว้ถูกล้าง
  ทันทีที่ sign out หน้าต่างแก้ไขที่เปิดค้างอยู่ก็ถูกปิด (เดิมเฉพาะหน้า Overview ที่หายไปถูกต้อง)
- ทดสอบด้วย Edge จาก IP ของ LAN: sign in → ไปแต่ละหน้าทั้ง 7 หน้า → sign out → ไม่เหลือ panel ใดบนจอ เหลือแค่ข้อความให้ sign in

## 1.5.1 (2026-10-07)

- แก้: เมื่อเปิด **Sign-in required to view** แล้วเปิด dashboard จากเครื่องอื่นโดยยังไม่ sign in หน้าเว็บยังเรียก `/api/status` ทุก 2 วินาที
  และได้ 401 ทุกครั้ง (เห็นเป็นแถวสีแดงเต็ม DevTools) ตอนนี้หน้าเว็บหยุดเรียกข้อมูลจนกว่าจะ sign in
  - `/api/me` มี `canView` บอกว่าผู้ที่เปิดอยู่ดูข้อมูลได้หรือไม่ หน้าเว็บตรวจตัวนี้ก่อน ไม่ต้องลองเรียกแล้วโดนปฏิเสธ
  - ระหว่างรอ sign in มีแค่การถาม `/api/me` ทุก 30 วินาที (เพื่อรู้ว่ามีการ sign in จากแท็บอื่น); sign in เสร็จหน้าเว็บโหลดข้อมูลทันที
  - ป้าย ON / OFF ที่มุมซ้ายบนไม่แสดงระหว่างนั้น (เดิมขึ้น OFF สีแดง ทั้งที่ name server ทำงานอยู่ - หน้าเว็บแค่ไม่มีสิทธิ์ถาม)
- ทดสอบ: ชุดทดสอบทั้งสามชุดบน Windows และ Debian 13 (WSL); เปิดหน้าเว็บจาก IP ของ LAN ด้วย Edge แล้วนับ request:
  ระหว่างรอ sign in 8 วินาที = 0 request, หลัง sign in = ข้อมูลกลับมาตามปกติ, sign out แล้ว = 0 request อีกครั้ง

## 1.5.0 (2026-10-06) - เวอร์ชัน Linux

ผ่าน `nbns-smoke.ps1`, `replication-smoke.ps1`, `security-smoke.ps1` ทั้งบน Windows (Native AOT exe) และบน **Debian 13 ใน WSL**
(Native AOT linux-x64, รันชุดทดสอบด้วย pwsh); ติดตั้งเป็น systemd service จริงใน WSL แล้วทดสอบปุ่ม Restart service และ uninstall

### เพิ่ม

- **รันบน Linux ได้** - Debian 13, Ubuntu 26.04 / 24.04, CentOS Stream 10 (ทดสอบแล้วเฉพาะ Debian 13)
  - `Output\WinsAlt_v1.5.0_linux-x64.tar.gz`: binary `winsalt`, `install.sh`, `uninstall.sh`, `winsalt.service`
  - systemd: `Type=notify`, log ไป journald, รันเป็น root ภายใต้ sandbox ของ systemd, `Conflicts=nmbd.service`
  - **ปุ่ม Restart service** บน Linux: process หยุดตามปกติด้วย exit code 75 แล้ว systemd start ใหม่
  - **ตาราง Firewall บน Linux**: กด Allow / Remove ได้กับ **firewalld** (`firewall-cmd`, ทั้ง runtime และ permanent ใน default zone)
    และ **ufw**; ไม่มีทั้งสอง = แสดงว่าไม่มี firewall ที่บล็อก port - **ยังไม่ได้กดจริงกับ firewalld / ufw** (WSL ไม่มี)
  - ชื่อ workgroup อ่านจาก `/etc/samba/smb.conf` ถ้ามี
  - ข้อความเมื่อ UDP 137 ถูกใช้อยู่บน Linux แนะนำให้ปิด `nmbd` (หรือบอกว่าต้องเป็น root)
- `build-linux.ps1` / `build-linux.sh`: build ใน WSL (.NET 10 SDK แยกไว้ที่ `/opt/dotnet10`)
- `/api/firewall` มี `firewall` (`Windows Firewall` / `firewalld` / `ufw` / `none`); `/api/settings` มี `serviceManager`

### เปลี่ยน

- Windows: ไม่มีพฤติกรรมเปลี่ยน - ตาราง Firewall ขึ้นชื่อ firewall ที่ใช้ และหน้าเว็บใช้ข้อความตาม OS
- ชุดทดสอบรันบน Linux ด้วย `pwsh` ได้; `nbns-smoke.ps1` ไม่ล้มเมื่อไม่ได้ใส่ `-Dashboard`
- `security-smoke.ps1` หา IP ของ LAN จาก adapter แทน DNS (Debian ชี้ชื่อเครื่องไปที่ 127.0.1.1)

## 1.4.4 (2026-10-06)

- เปลี่ยน: หน้าต่างคำอธิบายในหน้า Settings จัดเยื้องให้ตรงกัน - เครื่องหมาย • และเลขข้ออยู่ในคอลัมน์ของตัวเอง
  ข้อความที่ยาวจนตัดบรรทัดจึงต่อใต้ข้อความ ไม่ย้อนไปชิดขอบซ้าย; บรรทัดย่อยใต้ตัวเลือกเยื้องเท่ากันและใช้สีจางกว่า

## 1.4.3 (2026-10-06)

- เปลี่ยน: **คำอธิบายในหน้า Settings ทั้ง 35 หัวข้อเขียนใหม่ให้แบ่งบรรทัด** - หนึ่งย่อหน้าต่อหนึ่งเรื่อง, ตัวเลือกแต่ละตัว (เช่น Challenge /
  Overwrite / Reject หรือ ON / OFF) ขึ้นบรรทัดของตัวเองนำด้วย •, ขั้นตอนเป็นเลขข้อ, ข้อควรระวังแยกเป็นย่อหน้าท้าย
  (เดิมทุกอย่างต่อกันเป็นย่อหน้าเดียวคั่นด้วย ,) หน้าต่างคำอธิบายแสดงการขึ้นบรรทัดตามที่เขียน

## 1.4.2 (2026-10-06)

- เปลี่ยน: หัวข้อแบบเปิด/ปิดในหน้า Settings ใช้ **สวิตช์ ON / OFF** แทน checkbox (เขียว + ON = เปิด, เทา + OFF = ปิด)
  ยังเป็น checkbox จริงอยู่ข้างใต้ จึงใช้ Tab / Space และ screen reader ได้เหมือนเดิม
- ยืนยันบนเครื่องจริง (`SERVER-A`, Windows 10): ปุ่ม **Restart service** ของ 1.4.1 ทำงานได้

## 1.4.1 (2026-10-06)

- เพิ่ม: **ปุ่ม Restart service ในหน้า Settings** - ไม่ต้องไป restart ที่ Windows Services เอง
  - อยู่ในแถบเตือน "Restart the WinsAlt service to apply: ..." และใน panel *WinsAlt service* ท้ายหน้า; ต้อง sign in และกดยืนยันก่อน
    (หน้าต่างยืนยันบอกว่าค่าใดจะมีผล, name server หยุดตอบไม่กี่วินาที, ทุกคนถูก sign out)
  - ระหว่าง restart หน้าเว็บแสดง "Restarting..." แล้วต่อกลับเองเมื่อ service ขึ้น; ถ้าไม่ขึ้นภายใน 1 นาทีจะแจ้งให้ไปดูที่เครื่อง server
  - วิธีทำงาน: service เรียก `WinsAlt.exe --restart-service` เป็น process แยก ซึ่งสั่ง Service Control Manager ให้ stop (เป็นการ stop ปกติ
    ฐานข้อมูลถูกบันทึกตามเดิม) รอจนหยุด แล้ว start ใหม่ (ลองซ้ำ 3 ครั้ง) - เรียก API ของ Windows โดยตรง ไม่ใช้ cmd / net / PowerShell
    ผลการทำงานถูกเขียนลง `service-restart.log` ข้าง exe
  - ถ้ารันจาก console (ไม่ใช่ service) ปุ่มจะไม่แสดง
- API: `POST /api/service/restart` (admin); `/api/info` มี `startedAt`, `/api/settings` มี `canRestart`
- ตอนออกรุ่นยังไม่ได้ทดสอบการ restart จริงของ service (ยืนยันบน `SERVER-A` ภายหลัง - ดู 1.4.2) ที่ทดสอบบนเครื่อง dev:
  ตัวช่วยเรียก Service Control Manager ได้จริงบน native exe (ได้ข้อความ "service does not exist" ตามคาด), การปฏิเสธเมื่อไม่ sign in /
  ไม่ใช่ service, และฝั่งหน้าเว็บ (ปุ่ม → ยืนยัน → process ถูกหยุดแล้วเปิดใหม่จริง → หน้าเว็บต่อกลับและแสดงค่าที่มีผลแล้ว)

## 1.4.0 (2026-10-06) - ตั้งค่าจาก dashboard + ดูอย่างเดียวเมื่อไม่ sign in

ผ่าน `security-smoke.ps1` (เพิ่มชุด view-only และ settings), `nbns-smoke.ps1`, `replication-smoke.ps1` บน Native AOT exe
และเดินหน้าจอจริงด้วย Edge (Playwright): ดูอย่างเดียว → ตั้งรหัส → sign in → แก้ค่า → บันทึก → sign out

### พฤติกรรมที่เปลี่ยน - อ่านก่อนอัปเกรดเครื่องจริง

1. **ไม่ sign in = ดูได้อย่างเดียว ทุกเครื่อง รวมถึงเครื่อง server เอง** เดิมถ้ายังไม่ตั้งรหัสผ่าน เครื่อง server แก้ไขได้ทุกอย่างโดยไม่ต้อง sign in
   ตอนนี้สิ่งเดียวที่เครื่อง server ทำได้โดยไม่ sign in คือ **ตั้งรหัสผ่านครั้งแรก** → server ที่ยังไม่ได้ตั้งรหัส ต้องตั้งก่อนจึงจะแก้ไขอะไรได้
2. สคริปต์ที่เรียก API จากเครื่อง server โดยอาศัยข้อ 1 เดิม จะได้ 401 - ต้องตั้ง API token แล้วส่ง `X-Admin-Token`

### เพิ่ม

- **แท็บ Settings**: แก้ค่าได้ 35 หัวข้อจากหน้าเว็บ ไม่ต้องแก้ไฟล์ `.json` - Name server, Names and conflicts, This server's own name,
  Security, DNS fallback, Replication, Dashboard
  - ทุกหัวข้อมีปุ่ม **?** เปิดหน้าต่าง (modal) คำอธิบายภาษาไทย พร้อมค่าเริ่มต้น, ช่วงค่าที่รับ, ชื่อ key และบอกว่ามีผลทันทีหรือต้อง restart
  - **มีผลทันทีโดยไม่ต้อง restart**: โหมดการลงทะเบียน, allowed subnets, ชื่อต้องห้าม, quota ชื่อ, TTL, conflict policy,
    ตอบ broadcast, DNS ทั้งหมด, sign-in เพื่อดู, host name ที่อนุญาต, API token
  - **ต้อง restart service** (มีป้าย `restart`): listen address, port, ขนาดคิว/query log, รอบ sweep/บันทึก, rate limit, จำนวน challenge,
    ชื่อ/suffix/กลุ่มของเครื่อง, replication port - หน้าเว็บแสดงแถบเตือนและค่าที่ยังรันอยู่จนกว่าจะ restart
  - ก่อนบันทึกมีหน้าต่างยืนยันที่แสดงค่าเดิม → ค่าใหม่ของทุกหัวข้อที่เปลี่ยน
  - ค่าผิดช่วง/ผิดรูปแบบถูกปฏิเสธพร้อมเหตุผล (ไม่ปรับค่าให้เอง) และไม่มีค่าใดถูกบันทึกจากการกดครั้งนั้น;
    โหมด `AllowedSubnets` ที่ไม่มี subnet บันทึกไม่ได้ (จะไม่มีเครื่องใดลงทะเบียนได้)
- ค่าถูกบันทึกลง **`settings.json`** ในโฟลเดอร์ข้อมูล มีลำดับเหนือ `appsettings.json` (ซึ่งไม่ถูกแก้) และใต้ environment variable
  ลบไฟล์นี้ = ยกเลิกทุกอย่างที่ตั้งจากหน้าเว็บ
- **เปิด dashboard ให้เครื่องอื่นเข้าได้จากหน้า Settings** (เดิมต้องแก้ `Kestrel:Endpoints:Management:Url` ในไฟล์):
  - สวิตช์ *Open the dashboard to other computers* (`Dashboard:RemoteAccess`) - เปิด = ฟังทุก address, ปิด = เฉพาะ 127.0.0.1;
    เปลี่ยนเฉพาะ address ส่วน port ยังเป็นค่าใน `appsettings.json`; ต้อง restart service
  - ตาราง **Windows Firewall on this server** ท้ายหน้า Settings: แสดงว่ามี rule ขาเข้าสำหรับ dashboard (TCP), name service (UDP 137)
    และ replication (TCP 8138) หรือยัง พร้อมปุ่ม **Allow / Remove** - rule ของ dashboard ชื่อ `WinsAlt dashboard`
    อีกสองตัวใช้ชื่อเดียวกับที่ installer สร้าง จึงเห็นสถานะของ rule เดิมด้วย; uninstall ลบ rule ของ dashboard ให้
- API: `GET /api/settings`, `PUT /api/settings` (admin); `GET /api/firewall`, `POST /api/firewall/{id}?allow=` (admin); `/api/me` มี `canSetPassword`
- หน้าเว็บ: ป้าย **View only** ต่อท้ายชื่อหน้า เมื่อไม่ได้ sign in และอยู่ในหน้าที่แก้ไขข้อมูลได้ (Overview, Registered names,
  Static mappings, Partner servers, Settings) และซ่อนปุ่มเพิ่ม/แก้ไข/ลบ/เปิด-ปิด ทั้งหมด (เดิมปุ่มยังแสดงแล้วค่อยถามรหัส)

### เปลี่ยน

- quota ชื่อ (`MaxDynamicNames`, `MaxNamesPerAddress`) และ `Dashboard:AllowedHosts` ถูกอ่านทุกครั้งที่ใช้ แทนการจำค่าไว้ตอน start
- ค่าแบบ list ใน config รับได้ทั้ง JSON array (เดิม) และค่าเดียวคั่นด้วยจุลภาค (รูปแบบที่หน้า Settings บันทึก)
- ชุดทดสอบทั้งสามส่ง `X-Admin-Token` (เดิมอาศัยสิทธิ์ของ localhost ตอนยังไม่ตั้งรหัส)

### ยังไม่ได้ทำ

- ปุ่ม restart service จากหน้าเว็บ (มาใน 1.4.1); พอร์ตของ dashboard, `Wins:DataDirectory`, `Wins:Workers`, `Logging` ยังแก้ใน `appsettings.json`
- ปุ่ม Allow/Remove ของ firewall **ยังไม่ได้กดจริง** - เครื่อง dev ไม่ได้รันแบบ administrator จึงทดสอบได้แค่การอ่านสถานะ,
  การปฏิเสธเมื่อไม่ sign in และหน้าต่างยืนยัน; คำสั่ง netsh ที่ใช้เป็นรูปแบบเดียวกับที่ installer ใช้อยู่
- ยังไม่ได้ทดสอบบน Windows 7 และยังไม่ได้ติดตั้งบนเครื่องจริง

## 1.3.2 (2026-10-06)

ชดเชยสิ่งที่เครื่อง server เสียไปเมื่อปิด NetBIOS over TCP/IP ของ Windows (เท่าที่ทำได้จากโปรแกรม):

- เพิ่ม: **ตอบ node status** (`nbtstat -A <ip>` และเครื่องมือสแกน network) แทนเครื่องที่รันอยู่ - คืนรายชื่อของเครื่องและ MAC ของ adapter
  ตอบเฉพาะ unicast และเฉพาะคำถามถึงชื่อ `*` หรือชื่อของเครื่องเอง ไม่ตอบแทนเครื่องอื่น
- เพิ่ม: **ลงทะเบียนเครื่องเป็นสมาชิกของ workgroup/domain** (`<ชื่อกลุ่ม><00>` แบบ group) เหมือนที่ Windows ทำเอง
  อ่านชื่อจาก Windows อัตโนมัติ หรือกำหนดด้วย `Wins:SelfGroup` (`"-"` = ไม่ลงทะเบียน)
- เพิ่ม: `Wins:SelfSuffixes` - กำหนด suffix ที่ประกาศชื่อเครื่อง (ค่าเริ่มต้น `00`, `20`; เพิ่ม `03` ได้)
  ไม่รับ `1B`/`1C`/`1D` เพราะเป็นบทบาทที่ service นี้ไม่ได้ทำ (domain master browser, domain controller, master browser)
- Dashboard: บรรทัด "Published as" ในหน้า Overview (suffix ที่ประกาศ + กลุ่มที่เป็นสมาชิก), Query log แสดง "Node status sent";
  `/api/status` มี `self.group` และ `self.suffixes`
- ทดสอบเพิ่มใน `nbns-smoke.ps1` (6 ข้อ): ตอบ node status, มีชื่อครบทุก suffix, มีชื่อกลุ่มแบบ group, ลงท้ายด้วย statistics 46 byte,
  ไม่ตอบแทนชื่อเครื่องอื่น - **ยังไม่ได้ทดสอบกับ `nbtstat -A` ของ Windows จริง** (ยิงได้เฉพาะ port 137) และยังไม่ได้เปิดดูหน้าจอ
- ยังชดเชยไม่ได้: บริการ datagram (UDP 138 - Network Neighborhood, mailslot), SMB ผ่าน TCP 139,
  และการที่เครื่อง server เองใช้ WINS หาชื่อ - ทั้งสามเป็นหน้าที่ของ driver ใน Windows

## 1.3.1 (2026-10-06)

- เปลี่ยน: **static mapping สำคัญกว่าเสมอ แม้อยู่คนละ server** ลำดับการตอบ client คือ
  static ในเครื่อง → static จาก replication → ชื่อที่ลงทะเบียนในเครื่อง → ชื่อจาก replication → partner → DNS
  (เดิมชื่อที่ลงทะเบียนในเครื่องชนะ static ของ WinsAlt เครื่องอื่น) และชื่อที่เป็น static ที่ server อื่นถูกป้องกันจากการลงทะเบียนทับที่นี่ด้วย
- เพิ่มการป้องกัน **cache poisoning**:
  - คำตอบจาก partner และ DNS ที่ไม่ใช่ IP ของเครื่องจริง (0.x, 127.x, multicast, broadcast) ไม่ถูกส่งต่อและไม่ถูก cache;
    ลงทะเบียน IP แบบนั้นก็ไม่ได้ (RCODE 5)
  - คำตอบ DNS ต้องมีคำถามตรงกับที่ถามไป (ชื่อ, type, class) จึงจะรับ
  - ปุ่ม **Clear cache** ในแท็บ Partner servers (`POST /api/cache/clear`) ล้างคำตอบที่ cache ไว้ทั้งหมดเมื่อสงสัยว่ามี IP ผิด
- ทดสอบเพิ่ม: คำตอบปลอมที่ transaction id ไม่ตรงถูกทิ้ง, คำตอบที่ชี้ multicast ถูกทิ้ง, ลำดับ static ข้าม server

## 1.3.0 (2026-10-06) - security audit + performance pass

ผ่าน `tools\security-smoke.ps1` (ใหม่, 44 ข้อ), `nbns-smoke.ps1` และ `replication-smoke.ps1` บน Native AOT exe

### พฤติกรรมที่เปลี่ยน - อ่านก่อนอัปเกรดเครื่องจริง

1. **ชื่อที่ WinsAlt เครื่องอื่นเป็นเจ้าของถูก challenge แล้ว** เดิมถ้าเครื่องนี้ไม่มีระเบียนของชื่อนั้น client ลงทะเบียนได้ทันที
   แม้ชื่อเดียวกันจะมีเจ้าของอยู่ที่อีก site (เห็นเป็น Replication) ตอนนี้ server จะถามเจ้าของตาม IP ในสำเนาก่อน
   ถ้าเจ้าของยังตอบ = ปฏิเสธ (นับใน Conflicts refused) แปลว่ามีเครื่องสองเครื่องใช้ชื่อเดียวกันข้าม site จริง → ต้องเปลี่ยนชื่อเครื่องหนึ่ง
2. **`WPAD` และ `ISATAP` ลงทะเบียนแบบ dynamic ไม่ได้ และไม่ถูกถามต่อไป partner/DNS** (ชื่อที่เครื่องมือ poisoning ใช้)
   ถ้าองค์กรใช้ WPAD ผ่าน WINS จริง ให้เพิ่มเป็น static mapping (static ยังตอบได้ตามปกติ) หรือกำหนด `BlockedNames: []`
3. **พอร์ต replication (TCP 8138) รับเฉพาะ IP ที่อยู่ในรายการ server** ถ้าเครื่องปลายทางมีหลาย adapter และออกด้วย IP อื่น
   จะขึ้น "peer closed the connection - this server is not in its server list yet" - ให้ใส่ IP ที่มันใช้ออกจริง
4. **Dashboard ตรวจ Host header**: เปิดด้วย IP, `localhost` หรือชื่อเครื่องได้เหมือนเดิม; ชื่อแบบ FQDN ต้องเพิ่มใน `Dashboard:AllowedHosts`
   (บรรทัด `"AllowedHosts": "*"` เดิมใน appsettings.json ไม่มีผลอะไรทั้งก่อนและหลัง - ลบได้)
5. **ทุกคำสั่งแก้ไขผ่าน API ต้องมี header `X-Requested-With: WinsAlt`** (dashboard ส่งให้เอง; สคริปต์ที่ใช้ `X-Admin-Token` ต้องเพิ่ม)
6. Packet ที่ยาวกว่า 576 byte ถูกทิ้ง และแต่ละ IP ส่งได้ 100 request/วินาที (burst 300) - client ปกติส่งไม่กี่ packet ต่อนาที

### Security - ช่องโหว่ที่แก้

- **CSRF ใน setup mode**: ตอนยังไม่ตั้งรหัสผ่าน เครื่อง server เป็น admin โดยไม่มี cookie หน้าเว็บใดก็ตามที่เปิดใน browser บนเครื่อง server
  สั่ง `POST /api/netbt/{id}?enabled=...` (ไม่มี body = ไม่มี preflight) ได้ → ตอนนี้ทุกคำสั่งแก้ไขต้องมี custom header
- **DNS rebinding**: domain ของผู้โจมตีที่ชี้มา 127.0.0.1 กลายเป็น same-origin กับ dashboard → เพิ่มการตรวจ Host header
- **Challenge ไม่จำกัดจำนวน**: registration ปลอมจำนวนมากทำให้ server ยิง query ไปหาเหยื่อ + ส่ง WACK ได้ไม่จำกัด (reflector) → จำกัด 64 พร้อมกัน
- **Transaction id เดาได้**: challenge และ partner lookup ใช้ตัวนับ +1 ผู้ที่เห็น id หนึ่งครั้งปลอมคำตอบครั้งถัดไปได้
  (รวมถึงคำตอบ "เครื่องเดียวกัน" ที่ทำให้ IP ผู้โจมตีถูกเพิ่มใต้ชื่อเหยื่อ) → สุ่มด้วย CSPRNG ทุกครั้ง
- **ฐานข้อมูลโตได้ไม่จำกัด**: ลงทะเบียนชื่อสุ่มไปเรื่อย ๆ จน memory หมด → จำกัด 100,000 ชื่อ และ 64 ชื่อต่อ IP
- **Log flood**: ทุก packet ที่ถูกปฏิเสธเขียน Windows Event Log หนึ่งบรรทัด → จำกัดอัตราการเขียน (ตัวนับและ Query log ยังครบ)
- **พอร์ต replication เปิดให้ทุกคน**: ใครก็ต่อเข้ามารับ nonce แล้วเดา key หรือค้าง connection ไว้ได้ → รับเฉพาะ peer + จำกัดเวลารอ 5 วินาที
- **ชื่อที่ site อื่นเป็นเจ้าของถูกยึดได้** จาก site นี้ (ข้อ 1 ด้านบน)

### Security - สิ่งที่เพิ่ม (section `Wins:Security`)

- `RegistrationMode`: `Dynamic` (ค่าเริ่มต้น) | `AllowedSubnets` | `StaticOnly`; `AllowedSubnets` (CIDR); `RequireAddressMatchesSource`
- `BlockedNames`, `MaxDynamicNames`, `MaxNamesPerAddress`, `RateLimitPerSecond` / `RateLimitBurst`, `MaxConcurrentChallenges`, `AllowMultihomedMerge`
- `Dashboard:RequireSignInToView`, `Dashboard:AllowedHosts`; response header: CSP, `X-Frame-Options: DENY`, `nosniff`, `no-referrer`, `no-store`
- Kestrel: request body ≤ 64 KB, header ≤ 16 KB, ≤ 200 connection, ไม่ส่ง `Server` header
- Dashboard: panel "Security policy" + ตัวนับ Refused by policy / Blocked-name attempts / Rate limited

### Performance

- `QueryLog` เป็น lock-free (เดิมทุก worker แย่ง lock เดียวทุก packet) - จุด contention สุดท้ายบน hot path
- ถอดรหัสชื่อ NetBIOS ลง `ulong` สองตัวโดยตรง (ไม่มี buffer กลาง, bounds check ครั้งเดียวต่อชื่อ); `NameKey` มี `Equals`/`GetHashCode` เขียนเอง
- `AggressiveInlining` เฉพาะ leaf เล็ก ๆ บนเส้นทาง parse; `<OptimizationPreference>Speed</OptimizationPreference>`
- เพิ่ม `metrics.allocatedBytes` / `gen0Collections` ใน `/api/status` และ `WinsAlt.exe --bench <ip> <port> <วินาที>`
- วัดบนเครื่อง dev (loopback, client 4 thread, ปิด rate limit, client กับ server อยู่เครื่องเดียวกัน):
  **66,502 query/วินาที, เฉลี่ย 60 µs ต่อคำตอบ, ไม่มี packet หล่น, 1.89 byte ของ allocation ต่อ query**
  (ตัวเลข allocation รวมการเรียก `/api/status` และการเก็บสถิติทุก 2 วินาที - เส้นทาง packet เองไม่ allocate), memory 25.8 MB

## 1.2.0 (2026-10-06)

- ปรับ UI ทั้ง dashboard ให้เป็นแบบเดียวกับ an earlier dashboard project: top bar + โลโก้ + ป้าย ON/OFF, tile ตัวเลขหลัก,
  panel/ตารางแบบเดียวกัน, ปุ่มไอคอน (แก้ไข / ลบ / หยุด-เล่น), ฟอนต์ JetBrains Mono ฝังในไฟล์, footer สลับ theme
- เพิ่ม: **Modal** สำหรับเพิ่ม/แก้ static mapping, เพิ่ม server (replication และ query forwarding), replication key
- เพิ่ม: **Popup ยืนยัน**ก่อนทุกการลบและก่อนเปิด/ปิด NetBIOS - บอกผลที่จะเกิดขึ้นเป็นข้อ ๆ
- เพิ่ม: **Sign in** (บัญชี `admin`, PBKDF2 ใน `auth.json`, session cookie 8 ชั่วโมง, ล็อก IP 15 นาทีเมื่อผิด 5 ครั้ง)
  เมนูผู้ใช้: Change password, Sign out - ไม่มีรหัสผ่านเริ่มต้น: จนกว่าจะตั้งรหัส แก้ไขได้เฉพาะจากเครื่อง server เอง
  (พฤติกรรมเดิม) และ dashboard จะเตือนให้ตั้ง; หลังตั้งแล้วทุกการแก้ไขต้อง sign in แม้จากเครื่อง server
- เปลี่ยน: ช่องกรอก admin token ใน dashboard ถูกแทนด้วย sign in (`Dashboard:AdminToken` + header `X-Admin-Token`
  ยังใช้ได้สำหรับสคริปต์)
- ตรวจแล้วด้วย Playwright: ทุก modal, popup ยืนยัน, ตั้งรหัส → sign in → sign out, theme สว่าง, และความกว้างมือถือ 390px
  (ไม่มี scroll แนวนอน)

## 1.1.4

- แก้: ตาราง Registered names และ Static mappings - แถวที่มี IP หลายตัว (เช่น `__MSBROWSE__`) ดันคอลัมน์
  Source / Type / Expires ออกนอกจอ ตอนนี้ช่อง Address ขึ้นบรรทัดใหม่เอง

## 1.1.3

- เพิ่ม: คอลัมน์และตัวกรอง **Source** ในหน้า Registered names - `Local`, `Replication from <server>`,
  `Partner via <server>` (แถว Partner คือคำตอบที่ cache ไว้ 5 นาที ไม่ใช่ระเบียนในฐานข้อมูล)
- API: `/api/names?source=local|replication|partner` (`type=replica` เดิมยังใช้ได้)

## 1.1.2

- แก้: ปุ่ม Turn off/Turn on NetBIOS ใช้ไม่ได้บน Windows 7 / Server 2008 R2 ("Windows no longer lists that adapter")
  เพราะเรียก `Get-CimInstance` ซึ่ง PowerShell 2.0 ไม่มี - เปลี่ยนเป็น `Get-WmiObject`

## 1.1.1

- เพิ่ม: ปุ่ม **Show key** ดู replication key ที่บันทึกไว้ (เฉพาะ admin) และช่องติ๊ก "Show what I type"
- API: `GET /api/replication/key` (admin เท่านั้น; หน้าสถานะสาธารณะไม่เปิดเผย key)

## 1.1.0

- เพิ่ม: **Replication ระหว่าง WinsAlt ด้วยกัน** - แต่ละเครื่องดึง snapshot ของชื่อที่เครื่องอื่นเป็นเจ้าของทุก 30 วินาที
  ผ่าน TCP 8138 ยืนยันสองทางด้วย HMAC-SHA256 จาก replication key ร่วม; ชื่อ local ชนะสำเนาเสมอ;
  `<1C>` รวมสมาชิกจากทุก site; installer เพิ่ม firewall rule TCP 8138
- แก้: คำตอบ "ไม่พบ" ของ Microsoft WINS (ทุก count เป็น 0 แล้วตามด้วยชื่อ) อ่านไม่ออก จึงถูกนับเป็น No reply
  และ client ต้องรอ timeout 1.5 วินาทีทุกครั้ง
- เพิ่ม: partner ที่เงียบ 3 ครั้งติดจะไม่ถูกรอคำตอบทุก query - ลองใหม่ทุก 30 วินาทีจนกว่าจะตอบ
- เพิ่ม: `tools\replication-smoke.ps1` (เปิด WinsAlt สองตัว ตรวจ 13 ข้อ)

## 1.0.5

- เพิ่ม: **Query forwarding ไปยัง WINS server อื่น** (แท็บ Partner servers) - ชื่อที่ไม่พบจะถูกถามต่อด้วย
  name query ธรรมดาบน UDP 137 ใช้ได้กับ Microsoft WINS; ลำดับการหา: local → partner → DNS; มีสถิติต่อ partner

## 1.0.4

- เพิ่ม: หน้า Static mappings เลือก suffix ด้วย checkbox พร้อมชื่อบทบาท แทนการพิมพ์ hex
- เพิ่ม: หน้า Registered names กรองตาม Role (suffix)
- แก้: เครื่องเดียวกันที่ต่อทั้ง LAN และ Wi-Fi ถูกปฏิเสธว่า "name in use" - ตอน challenge ถ้าคำตอบของเจ้าของเดิม
  มี IP ของผู้ขอใหม่อยู่ด้วย ถือว่าเป็นเครื่องเดียวกันและเก็บทั้งสอง IP (Multihomed)

## 1.0.3

- แก้: installer ถูก Windows Defender บล็อกแบบ `Behavior:Win32/DefenseEvasion.A!ml` - เลิกสร้างและรันสคริปต์
  PowerShell (`-ExecutionPolicy Bypass`), เลิกลบแล้วสร้าง service ใหม่ตอน upgrade, เลิกลบ firewall rule เดิม
  การหาพอร์ตว่างและตรวจ listener ย้ายเข้า `WinsAlt.exe --pick-port` / `--listener-state`
- ยังเหลือ: Defender จัดไฟล์ installer เป็น `Trojan:Win32/Bearfoos.B!ml` (ML จากตัวไฟล์ที่ไม่ได้ sign) - ดู TODO

## 1.0.2

- เพิ่ม: ประกาศชื่อเครื่องของ server เองโดยอัตโนมัติ (suffix `00` + `20`) และตาม IP เมื่อเปลี่ยน (`Wins:RegisterSelf`)
- เพิ่ม: ตาราง "Windows NetBIOS over TCP/IP on this server" พร้อมปุ่ม Turn off / Turn on ต่อ adapter

## 1.0.1

- แก้ (สำคัญ): listener ขึ้น **Active ปลอม** - bind `0.0.0.0:137` สำเร็จทั้งที่ NetBT ของ Windows ยังถือ
  `<ip>:137` อยู่ ทำให้ได้รับแต่ broadcast ไม่ได้รับ request จริงเลย ตอนนี้ bind แบบ exclusive
  ถ้า NetBT ยังอยู่จะขึ้น Error พร้อมข้อความบอกสาเหตุ

## 1.0.0

- NBNS server บน UDP 137: registration, refresh, release, query; ชื่อ unique / group / internet group `<1C>` / multihomed
- Name challenge แบบ WINS เมื่อชื่อ unique ถูกขอจาก IP อื่น (WACK → ถามเจ้าของเดิม → ให้หรือปฏิเสธ)
- Name database ใน memory, หมดอายุตาม TTL, บันทึก snapshot ลง `wins-db.json`
- Static mapping (`static-mappings.json`, reload เมื่อแก้ไฟล์), DNS fallback
- Dashboard ฝังใน exe (Vue 3): Overview, Registered names, Static mappings, Query log, Events
- Native AOT exe เดียว รันเป็น Windows Service หรือ console; installer (Inno Setup)
