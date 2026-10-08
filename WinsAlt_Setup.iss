; ============================================================================
; WinsAlt_Setup.iss  -  Inno Setup script for WinsAlt NetBIOS Name Server
; ----------------------------------------------------------------------------
; Installs the Native AOT exe (NBNS listener on UDP 137 + embedded dashboard)
; as a Windows Service (created with sc.exe).
;
; What this script does beyond copying files:
;   * Auto-picks a free dashboard port: prefers the one already in
;     appsettings.json (http://0.0.0.0:8137 by default since 1.6.0); if it is busy it
;     falls back to 8138/8139/18137/9137 and writes the chosen port back into
;     appsettings.json + the desktop shortcut before the service starts.
;   * Runs NO scripts and NO PowerShell: the port check and the listener
;     check are answered by WinsAlt.exe itself (--pick-port / --listener-state).
;     An upgrade stops the service, replaces the exe and starts it again; it
;     does not delete/re-create the service or touch an existing firewall rule.
;   * Optional Windows Firewall rules for inbound UDP 137 (the name service), TCP 8138
;     (replication) and the dashboard port. Since 1.6.0 a fresh install opens the dashboard to
;     the LAN (sign-in required to view, admin / admin until changed); an upgrade keeps its
;     appsettings.json, so a dashboard that was localhost-only stays so and gets no rule.
;   * After starting the service it asks the service whether the UDP 137
;     listener came up, and says so plainly if it did not - the usual cause is
;     Windows' own "NetBIOS over TCP/IP" still holding the port. The installer
;     deliberately does NOT switch NetBIOS off itself: that changes how this
;     machine is seen on the network and is the operator's call.
;   * Upgrades keep appsettings.json, static-mappings.json and wins-db.json.
;
; BUILD:  powershell -ExecutionPolicy Bypass -File build-installer.ps1
;   (= build-aot.ps1, then ISCC on this file)
; Output installer -> Output\WinsAlt_Setup_v<MyAppVersion>.exe
;
; Keep MyAppVersion in step with <Version> in WinsAlt.csproj.
; ============================================================================

#define MyAppName "WinsAlt NetBIOS Name Server"
#define MyAppVersion "1.8.2"
#define MyAppExeName "WinsAlt.exe"
#define MyServiceId "WinsAlt"
#define MyServiceDisplayName "WinsAlt NetBIOS Name Server"
#define MyFirewallRule "WinsAlt NBNS (UDP 137)"
#define MyReplFirewallRule "WinsAlt replication (TCP 8138)"
#define MyDashFirewallRule "WinsAlt dashboard"
#define MyShortcut "WinsAlt Dashboard.url"
#define MyPublishDir "publish\win-x64"

[Setup]
; Unique to this product (do NOT reuse another app's AppId)
AppId={{5D2B8E71-9C4A-4F36-A1E0-73B6C9D4F218}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={autopf}\WinsAlt
DefaultGroupName=WinsAlt
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename=WinsAlt_Setup_v{#MyAppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
; MIT license page (accept / do not accept), then the scope-and-limitations page from [Code] (accept required).
LicenseFile=LICENSE
AppPublisher=wattich
; English or Thai, picked by the user at start (defaults to the Windows UI language).
ShowLanguageDialog=auto
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\winsalt.ico
; The wizard itself carries the program icon too.
SetupIconFile=Assets\winsalt.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "thai"; MessagesFile: "compiler:Languages\Thai.isl"

[CustomMessages]
english.TaskFirewall=Allow inbound NetBIOS name service (UDP 137), WinsAlt replication (TCP 8138) and the dashboard through Windows Firewall
thai.TaskFirewall=อนุญาต NetBIOS name service (UDP 137), replication ของ WinsAlt (TCP 8138) และ dashboard ผ่าน Windows Firewall
english.TaskDesktop=Create a desktop shortcut to the dashboard
thai.TaskDesktop=สร้าง shortcut ไปยัง dashboard บน desktop
english.TermsTitle=Scope and limitations of use
thai.TermsTitle=ขอบเขตและข้อจำกัดการใช้งาน
english.TermsSubtitle=Please read the following before installing WinsAlt.
thai.TermsSubtitle=กรุณาอ่านข้อความต่อไปนี้ก่อนติดตั้ง WinsAlt
english.TermsAccept=I accept the scope and limitations of use
thai.TermsAccept=ฉันยอมรับขอบเขตและข้อจำกัดการใช้งาน
english.TermsDecline=I do not accept
thai.TermsDecline=ฉันไม่ยอมรับ
english.TermsRequired=To install WinsAlt, the scope and limitations of use must be accepted.
thai.TermsRequired=ต้องยอมรับขอบเขตและข้อจำกัดการใช้งานก่อน จึงจะติดตั้ง WinsAlt ได้
english.TermsFile=usage-terms.en.txt
thai.TermsFile=usage-terms.th.txt
english.DoneTitle=Installation complete.
thai.DoneTitle=ติดตั้งเสร็จแล้ว
english.DoneDashboard=Dashboard: %1
thai.DoneDashboard=Dashboard: %1
english.DoneLocal=%1  (this computer only)
thai.DoneLocal=%1  (เฉพาะเครื่องนี้)
english.DoneLan=%1  (from the LAN too)
thai.DoneLan=%1  (เครื่องอื่นใน LAN เปิดได้ด้วย)
english.DoneSignIn=Sign in:   admin / admin. Change the password straight after signing in.
thai.DoneSignIn=เข้าสู่ระบบ:   admin / admin แล้วเปลี่ยนรหัสผ่านทันที
english.DoneService=Service:   %1 (starts automatically on boot)
thai.DoneService=Service:   %1 (เริ่มเองเมื่อเปิดเครื่อง)
english.NoteListening=Name server: listening on UDP 137.%n%nPoint your clients' WINS server setting at this machine to start using it.
thai.NoteListening=Name server: รับ packet ที่ UDP 137 แล้ว%n%nตั้ง WINS server ของเครื่อง client เป็นเครื่องนี้เพื่อเริ่มใช้งาน
english.NoteNetbt=Name server: NOT listening yet. UDP 137 is still held by Windows' own NetBIOS over TCP/IP.%n%nOpen the dashboard and use "Turn off" in the table "Windows NetBIOS over TCP/IP on this server" for the adapter clients connect to. The name server starts listening a few seconds later, with no reinstall or restart.
thai.NoteNetbt=Name server: ยังไม่ได้รับ packet เพราะ NetBIOS over TCP/IP ของ Windows ยังถือ UDP 137 อยู่%n%nเปิด dashboard แล้วกด "ปิด" ในตาราง "NetBIOS over TCP/IP ของ Windows บนเครื่องนี้" ที่ adapter ที่ client ใช้ name server จะเริ่มรับ packet ภายในไม่กี่วินาที ไม่ต้องติดตั้งใหม่หรือ restart
english.NoteNoAnswer=The service was started, but the dashboard did not answer yet. Open the dashboard in a moment to check the listener state.
thai.NoteNoAnswer=เริ่ม service แล้ว แต่ dashboard ยังไม่ตอบ เปิด dashboard อีกสักครู่เพื่อดูสถานะ

[Tasks]
; No flags = checked by default (the operator can still untick before installing).
Name: "fwnbns"; Description: "{cm:TaskFirewall}"
Name: "desktopicon"; Description: "{cm:TaskDesktop}"

[Files]
; App payload: the single native exe. The dashboard UI lives inside it.
Source: "{#MyPublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
; Settings + static mappings: first install only, so the operator's edits survive upgrades.
; wins-db.json (live registrations) is created at runtime and is never shipped or overwritten.
Source: "{#MyPublishDir}\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist
Source: "{#MyPublishDir}\static-mappings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist
; Icon of the desktop shortcut and of the Apps and features entry.
Source: "Assets\winsalt.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.th.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "INSTALL.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "INSTALL.th.md"; DestDir: "{app}"; Flags: ignoreversion
; License, third-party notices and the terms shown during setup, kept beside the program.
Source: "LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "installer\usage-terms.en.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "installer\usage-terms.th.txt"; DestDir: "{app}"; Flags: ignoreversion

[UninstallRun]
; Stop + delete the service (RunOnceId silences the duplicate-run warning).
Filename: "{sys}\net.exe"; Parameters: "stop {#MyServiceId}"; RunOnceId: "StopService"; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "delete {#MyServiceId}"; RunOnceId: "DeleteService"; Flags: runhidden
; Remove the firewall rule if it was added (harmless if it never existed).
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#MyFirewallRule}"""; RunOnceId: "DeleteFirewallRule"; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#MyReplFirewallRule}"""; RunOnceId: "DeleteReplFirewallRule"; Flags: runhidden
; Added by the installer (fresh installs, LAN dashboard) or from the dashboard's Settings page (FirewallService).
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#MyDashFirewallRule}"""; RunOnceId: "DeleteDashFirewallRule"; Flags: runhidden

[UninstallDelete]
; The desktop shortcut is created from [Code], so remove it explicitly.
Type: files; Name: "{autodesktop}\{#MyShortcut}"

; NOTE: appsettings.json, static-mappings.json and wins-db.json are left in place on uninstall
; on purpose, so a reinstall keeps the configuration. Delete {app} manually for a clean removal.

[Code]
var
  TermsPage: TWizardPage;
  TermsAccept, TermsDecline: TNewRadioButton;

// Shown after the license page. Next stays disabled until "I accept" is chosen, as on the license page.
procedure TermsChoiceClick(Sender: TObject);
begin
  WizardForm.NextButton.Enabled := TermsAccept.Checked;
end;

procedure InitializeWizard();
var memo: TNewMemo; lines: TArrayOfString; i: Integer; text: String;
begin
  TermsPage := CreateCustomPage(wpLicense, CustomMessage('TermsTitle'), CustomMessage('TermsSubtitle'));

  ExtractTemporaryFile(CustomMessage('TermsFile'));
  text := '';
  if LoadStringsFromFile(ExpandConstant('{tmp}\') + CustomMessage('TermsFile'), lines) then
    for i := 0 to GetArrayLength(lines) - 1 do
      text := text + lines[i] + #13#10;

  memo := TNewMemo.Create(TermsPage);
  memo.Parent := TermsPage.Surface;
  memo.Left := 0;
  memo.Top := 0;
  memo.Width := TermsPage.SurfaceWidth;
  memo.Height := TermsPage.SurfaceHeight - ScaleY(48);
  memo.ScrollBars := ssVertical;
  memo.ReadOnly := True;
  memo.WordWrap := True;
  memo.Text := text;

  TermsAccept := TNewRadioButton.Create(TermsPage);
  TermsAccept.Parent := TermsPage.Surface;
  TermsAccept.Left := 0;
  TermsAccept.Top := memo.Top + memo.Height + ScaleY(8);
  TermsAccept.Width := TermsPage.SurfaceWidth;
  TermsAccept.Caption := CustomMessage('TermsAccept');
  TermsAccept.OnClick := @TermsChoiceClick;

  TermsDecline := TNewRadioButton.Create(TermsPage);
  TermsDecline.Parent := TermsPage.Surface;
  TermsDecline.Left := 0;
  TermsDecline.Top := TermsAccept.Top + ScaleY(20);
  TermsDecline.Width := TermsPage.SurfaceWidth;
  TermsDecline.Caption := CustomMessage('TermsDecline');
  TermsDecline.Checked := True;
  TermsDecline.OnClick := @TermsChoiceClick;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = TermsPage.ID then
    WizardForm.NextButton.Enabled := TermsAccept.Checked;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = TermsPage.ID) and not TermsAccept.Checked then
  begin
    MsgBox(CustomMessage('TermsRequired'), mbError, MB_OK);
    Result := False;
  end;
end;

var
  gPort: Integer;

// index of the line holding the Kestrel endpoint ("Url": "http://<host>:<port>"), or -1
function FindUrlLine(const lines: TArrayOfString): Integer;
var i: Integer;
begin
  Result := -1;
  for i := 0 to GetArrayLength(lines) - 1 do
    if Pos('"Url"', lines[i]) > 0 then begin Result := i; Exit; end;
end;

// True when the dashboard listens on loopback only: the Url host in appsettings.json, unless the
// Settings page's "Open the dashboard to other computers" switch (settings.json) says otherwise.
function DashboardLocalOnly(): Boolean;
var lines: TArrayOfString; idx: Integer; raw: AnsiString; json: String;
begin
  Result := False;
  if LoadStringsFromFile(ExpandConstant('{app}\appsettings.json'), lines) then
  begin
    idx := FindUrlLine(lines);
    if idx >= 0 then
      Result := (Pos('127.0.0.1', lines[idx]) > 0) or (Pos('localhost', Lowercase(lines[idx])) > 0);
  end;
  if LoadStringFromFile(ExpandConstant('{app}\settings.json'), raw) then
  begin
    json := String(raw);
    StringChangeEx(json, ' ', '', True);
    if Pos('"Dashboard:RemoteAccess":"true"', json) > 0 then Result := False
    else if Pos('"Dashboard:RemoteAccess":"false"', json) > 0 then Result := True;
  end;
end;

// the port is the run of digits after the LAST ':' on the Url line (works for any bind host)
function PortOfLine(const s: string): Integer;
var i, lastColon: Integer; num: string;
begin
  lastColon := 0;
  for i := 1 to Length(s) do
    if s[i] = ':' then lastColon := i;
  num := '';
  i := lastColon + 1;
  while (lastColon > 0) and (i <= Length(s)) and (s[i] >= '0') and (s[i] <= '9') do
  begin
    num := num + s[i];
    i := i + 1;
  end;
  Result := StrToIntDef(num, 0);
end;

// the dashboard port currently in appsettings.json, default 8137
function ReadConfiguredPort(): Integer;
var lines: TArrayOfString; idx, v: Integer;
begin
  Result := 8137;
  if LoadStringsFromFile(ExpandConstant('{app}\appsettings.json'), lines) then
  begin
    idx := FindUrlLine(lines);
    if idx >= 0 then
    begin
      v := PortOfLine(lines[idx]);
      if v > 0 then Result := v;
    end;
  end;
end;

// // Both checks below are answered by WinsAlt.exe itself through its exit code (see
// InstallerCommands.cs). The installer deliberately runs no script and no PowerShell: generated
// scripts launched with -ExecutionPolicy Bypass are what antivirus behaviour monitoring flags.
function RunApp(const params: string; fallback: Integer): Integer;
var rc: Integer;
begin
  Result := fallback;
  if Exec(ExpandConstant('{app}\{#MyAppExeName}'), params, '', SW_HIDE, ewWaitUntilTerminated, rc) then
    Result := rc;
end;

// returns `pref` if it can be bound, otherwise the first bindable fallback port
function PickFreePort(pref: Integer): Integer;
begin
  Result := RunApp('--pick-port ' + IntToStr(pref), pref);
  if (Result < 1) or (Result > 65535) then Result := pref;
end;

// state of the freshly started service's UDP 137 listener: 0 listening, 1 not listening, 2 no answer yet
function QueryListenerState(port: Integer): Integer;
begin
  Result := RunApp('--listener-state ' + IntToStr(port), 2);
end;

function ExecOk(const exe, params: string): Boolean;
var rc: Integer;
begin
  Result := Exec(exe, params, '', SW_HIDE, ewWaitUntilTerminated, rc) and (rc = 0);
end;

procedure PatchConfigPort(oldP, newP: Integer);
var lines: TArrayOfString; idx: Integer;
begin
  if oldP = newP then Exit;
  if LoadStringsFromFile(ExpandConstant('{app}\appsettings.json'), lines) then
  begin
    idx := FindUrlLine(lines);
    if idx < 0 then Exit;
    StringChangeEx(lines[idx], ':' + IntToStr(oldP) + '"', ':' + IntToStr(newP) + '"', True);
    SaveStringsToFile(ExpandConstant('{app}\appsettings.json'), lines, False);
    Log('Dashboard port set to ' + IntToStr(newP) + ' (was ' + IntToStr(oldP) + ')');
  end;
end;

procedure ScExec(const params: string);
var rc: Integer;
begin
  if not Exec(ExpandConstant('{sys}\sc.exe'), params, '', SW_HIDE, ewWaitUntilTerminated, rc) then
    Log('sc.exe failed to launch: ' + params);
end;

procedure CreateDesktopShortcut(port: Integer);
var lines: TArrayOfString;
begin
  SetArrayLength(lines, 4);
  lines[0] := '[InternetShortcut]';
  lines[1] := 'URL=http://localhost:' + IntToStr(port);
  lines[2] := 'IconFile=' + ExpandConstant('{app}\winsalt.ico');
  lines[3] := 'IconIndex=0';
  SaveStringsToFile(ExpandConstant('{autodesktop}\{#MyShortcut}'), lines, False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var oldPort, state: Integer; note, exePath, where: string;
begin
  if CurStep = ssPostInstall then
  begin
    exePath := ExpandConstant('{app}\{#MyAppExeName}');

    // 1) decide the dashboard port (keep configured if free, else fall back) and write it back
    oldPort := ReadConfiguredPort();
    gPort := PickFreePort(oldPort);
    PatchConfigPort(oldPort, gPort);

    // 2) create the Windows Service on a first install; on an upgrade keep the existing one and
    //    only refresh its settings - an installed service is never deleted and re-created.
    //    (note: space required after each "key=")
    if ExecOk(ExpandConstant('{sys}\sc.exe'), 'query {#MyServiceId}') then
      ScExec('config {#MyServiceId} binPath= "' + exePath + '" start= auto DisplayName= "{#MyServiceDisplayName}"')
    else
      ScExec('create {#MyServiceId} binPath= "' + exePath + '" start= auto DisplayName= "{#MyServiceDisplayName}"');
    ScExec('description {#MyServiceId} "WINS replacement: NetBIOS name server (UDP 137) + dashboard"');
    ScExec('failure {#MyServiceId} reset= 86400 actions= restart/60000/restart/60000/restart/60000');

    // 3) optional firewall rule: inbound UDP 137 to this program only. Added once - an existing
    //    rule (from an earlier install) is left exactly as the operator has it.
    if WizardIsTaskSelected('fwnbns') then
      if not ExecOk(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall show rule name="{#MyFirewallRule}"') then
        ExecOk(ExpandConstant('{sys}\netsh.exe'),
          'advfirewall firewall add rule name="{#MyFirewallRule}" dir=in action=allow protocol=UDP localport=137 program="' +
            exePath + '" enable=yes profile=any');

    //    Same for the replication port other WinsAlt servers pull from (answers only with the shared key).
    if WizardIsTaskSelected('fwnbns') then
      if not ExecOk(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall show rule name="{#MyReplFirewallRule}"') then
        ExecOk(ExpandConstant('{sys}\netsh.exe'),
          'advfirewall firewall add rule name="{#MyReplFirewallRule}" dir=in action=allow protocol=TCP localport=8138 program="' +
            exePath + '" enable=yes profile=any');

    //    And the dashboard, when it listens beyond this machine (fresh installs since 1.6.0). Same
    //    name and scope as the Settings page's rule, so the page shows it as allowed.
    if WizardIsTaskSelected('fwnbns') and not DashboardLocalOnly() then
      if not ExecOk(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall show rule name="{#MyDashFirewallRule}"') then
        ExecOk(ExpandConstant('{sys}\netsh.exe'),
          'advfirewall firewall add rule name="{#MyDashFirewallRule}" dir=in action=allow protocol=TCP localport=' + IntToStr(gPort) +
            ' program="' + exePath + '" enable=yes profile=any');

    // 4) start the service (reads the patched dashboard port from appsettings.json)
    ScExec('start {#MyServiceId}');

    // 5) desktop shortcut to the dashboard
    if WizardIsTaskSelected('desktopicon') then
      CreateDesktopShortcut(gPort);

    // 6) check that the name server really is listening, and tell the operator the result
    state := QueryListenerState(gPort);
    if state = 0 then note := CustomMessage('NoteListening')
    else if state = 1 then note := CustomMessage('NoteNetbt')
    else note := CustomMessage('NoteNoAnswer');

    if DashboardLocalOnly() then
      where := FmtMessage(CustomMessage('DoneLocal'), ['http://localhost:' + IntToStr(gPort)])
    else
      where := FmtMessage(CustomMessage('DoneLan'), ['http://' + GetComputerNameString() + ':' + IntToStr(gPort)]);
    if not FileExists(ExpandConstant('{app}\auth.json')) then
      where := where + #13#10 + CustomMessage('DoneSignIn');

    MsgBox(
      CustomMessage('DoneTitle') + #13#10#13#10 +
      FmtMessage(CustomMessage('DoneDashboard'), [where]) + #13#10 +
      FmtMessage(CustomMessage('DoneService'), ['{#MyServiceId}']) + #13#10#13#10 + note,
      mbInformation, MB_OK);
  end;
end;

// Runs right AFTER the operator clicks Install, just BEFORE files are copied - the latest safe
// point to take the service down. net.exe stop BLOCKS until the service is fully STOPPED, so the
// app's graceful shutdown (final wins-db.json snapshot) completes and the file copy never races a
// still-locked WinsAlt.exe. Clients keep their cached answers and retry, so the outage is just
// stop + copy + start. The service itself stays installed.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var rc: Integer;
begin
  Result := '';  // empty = proceed with install
  // Blocks until STOPPED; on a fresh install (service absent) it returns immediately - harmless.
  Exec(ExpandConstant('{sys}\net.exe'), 'stop {#MyServiceId}', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Sleep(500); // brief grace for file handles to release before the copy
end;