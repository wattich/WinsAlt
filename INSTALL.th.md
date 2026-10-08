# คู่มือติดตั้ง WinsAlt

[English](INSTALL.md)

คู่มือสั้น ๆ ตั้งแต่ดาวน์โหลดจนเครื่อง client เริ่มหาชื่อได้ เรื่องอื่น (การตั้งค่า, replication, ความปลอดภัย)
ดูใน [README](README.th.md)

## 1. ดาวน์โหลด

ดาวน์โหลดไฟล์ของเวอร์ชันล่าสุดจาก[หน้า Releases](../../releases/latest)

| ระบบ | ไฟล์ |
|---|---|
| Windows 10 / 11, Windows Server 2012 ขึ้นไป (x64) | `WinsAlt_Setup_v<version>.exe` |
| Debian 13, Ubuntu 24.04 / 26.04 (x86_64) | `winsalt_<version>-1_amd64.deb` |
| CentOS Stream 10 และระบบตระกูล RHEL 10 (x86_64) | `winsalt-<version>-1.x86_64.rpm` |
| Linux ตามรายการข้างบน แบบไม่ใช้ package manager | `WinsAlt_v<version>_linux-x64.tar.gz` |

`SHA256SUMS.txt` มี checksum ของทุกไฟล์ ตรวจไฟล์ที่ดาวน์โหลดได้ด้วย

```bash
sha256sum -c SHA256SUMS.txt --ignore-missing          # Linux
```
```powershell
Get-FileHash .\WinsAlt_Setup_v<version>.exe            # Windows: เทียบกับบรรทัดใน SHA256SUMS.txt
```

สิ่งที่ต้องเตรียมก่อนติดตั้ง

- **UDP 137** บน server ต้องว่าง บน Windows ตัว "NetBIOS over TCP/IP" ของระบบถือ port นี้อยู่ (ขั้นที่ 3
  บอกวิธีปล่อย) บน Linux ต้องหยุด `nmbd` ของ Samba ก่อน
- **TCP 8138** ใช้ replication ระหว่าง server WinsAlt และ **TCP 8137** ใช้กับ dashboard (ถ้า 8137 ไม่ว่าง
  ตัวติดตั้งจะเลือก port อื่นให้)
- server Linux ควรตั้งชื่อเครื่องจริงก่อน (`hostnamectl set-hostname <ชื่อ>`)

## 2. ติดตั้ง

### Windows

1. คลิกขวาที่ `WinsAlt_Setup_v<version>.exe` แล้วเลือก Run as administrator ตัวติดตั้งยังไม่ได้ลง
   code signing ถ้า SmartScreen ขึ้นว่า "Windows protected your PC" ให้กด **More info** แล้วกด **Run anyway**
2. ยอมรับ License แบบ MIT แล้วยอมรับขอบเขตและข้อจำกัดการใช้งาน
3. ให้ติ๊กงาน firewall ไว้ ยกเว้นจะจัดการ Windows Firewall เอง (เปิด UDP 137, TCP 8138 และ port ของ
   dashboard ให้เฉพาะโปรแกรม WinsAlt)
4. ตอนจบตัวติดตั้งจะบอกที่อยู่ dashboard และบอกว่า name server รับ packet ที่ UDP 137 แล้วหรือยัง ถ้าติ๊ก
   งานสร้าง shortcut ไว้ จะมี shortcut ไปยัง dashboard บน desktop

โปรแกรมติดตั้งที่ `C:\Program Files\WinsAlt` และทำงานเป็น service ชื่อ `WinsAlt` (เริ่มเองเมื่อเปิดเครื่อง)

### Debian / Ubuntu (.deb)

```bash
sudo apt install ./winsalt_<version>-1_amd64.deb
```

### CentOS Stream / ตระกูล RHEL (.rpm)

```bash
sudo dnf install ./winsalt-<version>-1.x86_64.rpm
```

ทั้งสองแบบติดตั้ง package ที่ต้องใช้ให้เอง (OpenSSL 3, iproute, curl) ตรวจ port แล้วเริ่ม service `winsalt`
และเปิด port ใน ufw หรือ firewalld ถ้าเปิดใช้อยู่ บรรทัดท้าย ๆ ที่แสดงออกมาบอกที่อยู่ dashboard
ใช้กับ SELinux (Enforcing) ได้โดยไม่ต้องเพิ่ม policy

### Linux แบบไม่ใช้ package (.tar.gz)

```bash
tar -xzf WinsAlt_v<version>_linux-x64.tar.gz
cd winsalt-<version>
sudo ./install.sh          # --no-firewall: ไม่แตะ ufw / firewalld; --no-start: ติดตั้งอย่างเดียว
```

ผลเหมือนการติดตั้งจาก package: โปรแกรมอยู่ที่ `/opt/winsalt` ข้อมูลอยู่ที่ `/var/lib/winsalt`
service ชื่อ `winsalt` ดู log ด้วย `journalctl -u winsalt -f`

## 3. เข้าระบบครั้งแรก

1. เปิด dashboard ที่ `http://<ที่อยู่ server>:8137` (หรือ port ที่ตัวติดตั้งแจ้ง)
2. เข้าระบบด้วย **admin / admin** หน้าเว็บจะให้ตั้งรหัสผ่านใหม่ทันที ให้ตั้งก่อนทำอย่างอื่น
3. **เฉพาะ Windows:** ในหน้า **ภาพรวม** ตาราง "NetBIOS over TCP/IP ของ Windows บนเครื่องนี้" แสดง adapter
   ทั้งหมด ให้กด **ปิด** ที่ adapter ที่ client ใช้ name server จะเริ่มรับ packet ที่ UDP 137 ภายในไม่กี่วินาที
   และ WinsAlt จะประกาศชื่อของ server เอง

ภาษาของ dashboard เป็นไปตาม browser (ไทยหรืออังกฤษ) เปลี่ยนได้ที่ตัวเลือกด้านล่างของหน้า

## 4. ตั้ง client ให้ใช้ server นี้

ตั้ง WINS server ของเครื่อง client เป็น server นี้ ปกติตั้งผ่าน DHCP

- option **044** (WINS/NBNS servers) = ที่อยู่ของ server นี้ (ถ้ามี WinsAlt ตัวที่สอง ใส่เป็นลำดับที่สอง)
- option **046** (NetBIOS node type) = `0x8` (H-node)

client จะได้ค่าใหม่ตอนต่ออายุ DHCP (สั่ง `ipconfig /renew` เพื่อให้ได้ทันที) บนเครื่อง client คำสั่ง
`nbtstat -n` จะแสดงชื่อเป็น "Registered" และหน้า **ชื่อที่ลงทะเบียน** ใน dashboard จะมีชื่อนั้น

firewall และ router ระหว่างสาขาต้องยอมให้ UDP 137 ไปถึง server ทั้งขาไปและขากลับ

## 5. อัปเกรด

ติดตั้งเวอร์ชันใหม่ทับเวอร์ชันเดิมด้วยวิธีเดียวกับตอนติดตั้งครั้งแรก

- Windows: รัน `WinsAlt_Setup_v<version>.exe` ตัวใหม่
- `.deb`: `sudo apt install ./winsalt_<เวอร์ชันใหม่>-1_amd64.deb`
- `.rpm`: `sudo dnf install ./winsalt-<เวอร์ชันใหม่>-1.x86_64.rpm`
- `.tar.gz`: แตกไฟล์ตัวใหม่แล้วรัน `sudo ./install.sh`

การตั้งค่า, ชื่อ, static mapping, partner, replication และรหัสผ่านยังอยู่ครบ server ที่ติดตั้งจาก `.tar.gz`
อัปเกรดด้วย `.deb` หรือ `.rpm` ได้เลย เพราะใช้โฟลเดอร์เดียวกัน

## 6. ถอนการติดตั้ง

| ติดตั้งด้วย | ลบโปรแกรม เก็บข้อมูลไว้ | ลบทั้งหมด |
|---|---|---|
| ตัวติดตั้ง Windows | Apps and features, WinsAlt, Uninstall | แล้วลบโฟลเดอร์ `C:\Program Files\WinsAlt` |
| `.deb` | `sudo apt remove winsalt` | `sudo apt purge winsalt` |
| `.rpm` | `sudo dnf remove winsalt` | แล้วสั่ง `sudo rm -rf /var/lib/winsalt` |
| `.tar.gz` | `sudo ./uninstall.sh` | `sudo ./uninstall.sh --purge` |

บน Windows ถ้าจะให้ server กลับไปใช้ NetBIOS over TCP/IP ของระบบ ให้เปิดกลับที่ adapter หลังถอนการติดตั้ง

## 7. ถ้ามีปัญหา

| อาการ | สิ่งที่ต้องตรวจ |
|---|---|
| name server "ยังไม่ได้รับ packet" | Windows: NetBIOS over TCP/IP ยังเปิดอยู่ที่ adapter นั้น (ขั้นที่ 3) Linux: `nmbd` หรือโปรแกรมอื่นถือ UDP 137 อยู่ (`ss -ulpn 'sport = :137'`) |
| เปิด dashboard จากเครื่องอื่นไม่ได้ | firewall บน server (port ของ dashboard) หรือปิด "เปิด dashboard ให้เครื่องอื่น" ไว้ในหน้าตั้งค่า |
| client ไม่ลงทะเบียน | DHCP option 044, firewall ระหว่าง client กับ server (UDP 137) หรือนโยบายการลงทะเบียนในหน้าตั้งค่า |
| เข้าระบบไม่ได้เลย | ลบ `auth.json` ในโฟลเดอร์ข้อมูลแล้ว restart service จะกลับเป็น admin / admin |

log: Windows ดูใน Event Viewer (Windows Logs, Application) Linux ใช้ `journalctl -u winsalt`
