# =============================================================================
# security-smoke.ps1 - checks every security control end to end.
#
# Starts several WinsAlt instances on loopback test ports, each with a different
# Wins:Security configuration and a throw-away data folder, and attacks them the
# way the control is meant to stop: forged registrations, poisoning names, floods,
# oversized datagrams, cross-site requests, DNS rebinding, unauthenticated changes.
#
#   pwsh tools\security-smoke.ps1                      # uses publish\win-x64\WinsAlt.exe
#   pwsh tools\security-smoke.ps1 -Exe path\WinsAlt.exe
# =============================================================================
param(
    # Windows: publish\win-x64\WinsAlt.exe; Linux (pwsh): publish/linux-x64/winsalt
    [string]$Exe = (Join-Path $PSScriptRoot $(if ($IsLinux) { "../publish/linux-x64/winsalt" } else { "../publish/win-x64/WinsAlt.exe" }))
)

$ErrorActionPreference = "Stop"
$script:failed = 0
$script:trn = 0x6000
$script:nodes = @()
$script:nextPort = 0
$root = Join-Path ([System.IO.Path]::GetTempPath()) ("winsalt-sec-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$AdminToken = "smoke-test-admin-token"
$Csrf = @{ "X-Requested-With" = "WinsAlt" }                                   # passes the CSRF guard, but is nobody
$Admin = @{ "X-Requested-With" = "WinsAlt"; "X-Admin-Token" = $AdminToken }   # a script holding Dashboard:AdminToken
# Start-Process arguments for one instance: hidden window on Windows; on Linux the output goes to
# files in the instance's folder instead of this console (-WindowStyle does not exist there).
function Start-Arguments([string]$dir) {
    $a = @{ FilePath = $Exe; PassThru = $true }
    if ($IsLinux) { $a.RedirectStandardOutput = Join-Path $dir "stdout.log"; $a.RedirectStandardError = Join-Path $dir "stderr.log" }
    else { $a.WindowStyle = "Hidden" }
    return $a
}

# Starts one instance; $settings are extra environment overrides (Section__Key = value).
function Start-Node([string]$name, [hashtable]$settings = @{}) {
    $script:nextPort++
    $node = @{ Name = $name; Nbns = 11160 + $script:nextPort; Dash = 18160 + $script:nextPort }
    $dir = Join-Path $root $name
    [void][System.IO.Directory]::CreateDirectory($dir)
    $node.Dir = $dir

    $env:Wins__Port = $node.Nbns
    $env:Wins__ListenAddress = "127.0.0.1"
    $env:Wins__DataDirectory = $dir
    $env:Wins__ReplicationPort = 18180 + $script:nextPort
    $env:Wins__RegisterSelf = "false"
    $env:Wins__Dns__Enabled = "false"
    $env:Kestrel__Endpoints__Management__Url = "http://0.0.0.0:$($node.Dash)"
    $env:Dashboard__AdminToken = $AdminToken
    # An empty value means "not set at all": an empty variable would still override settings.json.
    foreach ($key in $settings.Keys) {
        if ("$($settings[$key])" -eq "") { Remove-Item -Path "Env:$key" -ErrorAction SilentlyContinue } else { Set-Item -Path "Env:$key" -Value $settings[$key] }
    }

    $startArgs = Start-Arguments $dir
    $node.Process = Start-Process @startArgs
    foreach ($key in $settings.Keys) { Set-Item -Path "Env:$key" -Value $null }

    $node.Api = "http://127.0.0.1:$($node.Dash)/api"
    $script:nodes += $node
    for ($i = 0; $i -lt 40; $i++) { try { [void](Invoke-RestMethod "$($node.Api)/info"); break } catch { Start-Sleep -Milliseconds 250 } }
    return $node
}

function U16([int]$v) { return [byte[]]@((($v -shr 8) -band 0xFF), ($v -band 0xFF)) }

function Encode-Name([string]$name, [byte]$suffix) {
    $raw = [System.Text.Encoding]::ASCII.GetBytes($name.ToUpperInvariant().PadRight(15)) + @($suffix)
    $out = New-Object System.Collections.Generic.List[byte]
    $out.Add(0x20)
    foreach ($b in $raw) { $out.Add([byte](0x41 + ($b -shr 4))); $out.Add([byte](0x41 + ($b -band 0x0F))) }
    $out.Add(0)
    return ,$out.ToArray()
}

# opcode 0 = query, 5 = registration, 6 = release.
function New-Request([int]$opcode, [string]$name, [string]$ip = "", [int]$padTo = 0) {
    $script:trn++
    $ar = if ($ip) { 1 } else { 0 }
    $pkt = (U16 $script:trn) + (U16 (($opcode -shl 11) -bor 0x0100)) + (U16 1) + (U16 0) + (U16 0) + (U16 $ar)
    $pkt += (Encode-Name $name 0x20) + (U16 0x20) + (U16 1)
    if ($ip) {
        $pkt += [byte[]]@(0xC0, 0x0C) + (U16 0x20) + (U16 1) + [byte[]]@(0, 4, 0x93, 0xE0) + (U16 6) + (U16 0x6000) + [System.Net.IPAddress]::Parse($ip).GetAddressBytes()
    }
    if ($padTo -gt $pkt.Length) { $pkt += New-Object byte[] ($padTo - $pkt.Length) }
    return ,[byte[]]$pkt
}

# Sends one request; returns the reply's RCODE, or -1 when the server stays silent.
function Rcode($node, [byte[]]$pkt, [int]$timeoutMs = 2500) {
    $udp = New-Object System.Net.Sockets.UdpClient
    try {
        $udp.Client.ReceiveTimeout = $timeoutMs
        [void]$udp.Send($pkt, $pkt.Length, "127.0.0.1", $node.Nbns)
        $remote = New-Object System.Net.IPEndPoint ([System.Net.IPAddress]::Any, 0)
        try { $r = $udp.Receive([ref]$remote); return ($r[3] -band 0x0F) } catch { return -1 }
    }
    finally { $udp.Close() }
}

# Data endpoints need a sign-in (or the token) by default since 1.6.0 - the server's own console included.
function Get-Api($node, [string]$path) { Invoke-RestMethod "$($node.Api)$path" -Headers $Admin }
function Counters($node) { (Get-Api $node "/status").counters }

# HTTP status of a request (200-range = the number, errors = their code).
function Http([string]$method, [string]$url, [hashtable]$headers = @{}, [string]$body = $null) {
    $args = @{ Method = $method; Uri = $url; Headers = $headers; SkipHttpErrorCheck = $true }
    if ($body) { $args.Body = $body; $args.ContentType = "application/json" }
    return (Invoke-WebRequest @args).StatusCode
}

function Check([string]$what, [bool]$ok, $detail = "") {
    if ($ok) { Write-Host "  PASS  $what" -ForegroundColor Green }
    else { Write-Host "  FAIL  $what  $detail" -ForegroundColor Red; $script:failed++ }
}

try {
    Write-Host "Security checks against $Exe" -ForegroundColor Cyan

    # ---------------------------------------------------------------- defaults
    Write-Host "`nDefault configuration" -ForegroundColor Cyan
    $d = Start-Node "default"
    $shipped = Get-Content (Join-Path (Split-Path $Exe) "appsettings.json") -Raw | ConvertFrom-Json
    Check "shipped appsettings.json: dashboard open to the LAN, sign-in required to view" (
        $shipped.Kestrel.Endpoints.Management.Url -match '^http://0\.0\.0\.0:' -and $shipped.Dashboard.RequireSignInToView -eq $true)
    # the installers' final check: needs no sign-in, so it reads the public /api/info
    $probe = Start-Process -FilePath $Exe -ArgumentList "--listener-state", "$($d.Dash)" -Wait -PassThru -NoNewWindow
    Check "the installer's listener check works without signing in (/api/info says Active, --listener-state exits 0)" (
        (Invoke-RestMethod "$($d.Api)/info").listener -eq "Active" -and $probe.ExitCode -eq 0) "exit=$($probe.ExitCode)"
    # the browser tab shows the server's name: /api/info tells it without signing in (short name, no domain part)
    $infoServer = (Invoke-RestMethod "$($d.Api)/info").server
    Check "/api/info names the server for the tab title, without signing in" (
        $infoServer -eq ([Environment]::MachineName.Split('.')[0].ToUpperInvariant())) "server='$infoServer'"

    Check "ordinary registration still works (RCODE 0)" ((Rcode $d (New-Request 5 "PC-OK" "10.50.0.1")) -eq 0)
    Check "WPAD cannot be registered (RCODE 5, refused)" ((Rcode $d (New-Request 5 "wpad" "10.50.0.66")) -eq 5)
    Check "ISATAP cannot be registered (RCODE 5)" ((Rcode $d (New-Request 5 "ISATAP" "10.50.0.66")) -eq 5)
    Check "WPAD does not resolve (RCODE 3)" ((Rcode $d (New-Request 0 "WPAD")) -eq 3)
    Check "blocked-name attempts are counted" ((Counters $d).blockedNames -eq 2)
    Check "a multicast address cannot be registered (RCODE 5)" ((Rcode $d (New-Request 5 "BOGUS1" "224.0.0.5")) -eq 5)
    Check "the broadcast address cannot be registered (RCODE 5)" ((Rcode $d (New-Request 5 "BOGUS2" "255.255.255.255")) -eq 5)
    Check "neither bogus name resolves" ((Rcode $d (New-Request 0 "BOGUS1")) -eq 3 -and (Rcode $d (New-Request 0 "BOGUS2")) -eq 3)

    Check "577-byte datagram is dropped unanswered" ((Rcode $d (New-Request 0 "PC-OK" "" 577) 700) -eq -1)
    Check "576-byte datagram is still served" ((Rcode $d (New-Request 0 "PC-OK" "" 576)) -eq 0)
    Check "oversized datagram is counted" ((Counters $d).oversized -eq 1)

    # truncated / corrupt packets must neither crash a worker nor get an answer
    foreach ($bad in @(([byte[]](1..5)), ([byte[]](0, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0x20, 0x41)), ([byte[]](0, 2, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0xC0, 0xFF)),
                       ((New-Request 5 "TRUNC" "10.50.0.9")[0..60]))) { [void](Rcode $d $bad 150) }
    Check "malformed packets are ignored and the server keeps answering" ((Rcode $d (New-Request 0 "PC-OK")) -eq 0 -and (Counters $d).malformed -ge 3)

    # ---------------------------------------------------------------- dashboard
    Write-Host "`nDashboard and API" -ForegroundColor Cyan
    $page = Invoke-WebRequest "http://127.0.0.1:$($d.Dash)/"
    Check "security headers on the page (CSP, no framing, no sniffing)" ($page.Headers["Content-Security-Policy"] -match "frame-ancestors 'none'" -and
        "$($page.Headers['X-Frame-Options'])" -eq "DENY" -and "$($page.Headers['X-Content-Type-Options'])" -eq "nosniff")
    Check "no Server header" (-not $page.Headers.ContainsKey("Server"))
    Check "API responses are not cacheable" ("$((Invoke-WebRequest "$($d.Api)/status" -Headers $Admin).Headers['Cache-Control'])" -eq "no-store")

    Check "DNS rebinding: a foreign Host name is refused (400)" ((Http GET "$($d.Api)/status" @{ Host = "evil.example.com" }) -eq 400)
    Check "the server's own name and IP literals are accepted" ((Http GET "$($d.Api)/status" @{ Host = "$([Environment]::MachineName)`:$($d.Dash)"; "X-Admin-Token" = $AdminToken }) -eq 200)

    Check "CSRF: bodyless POST without the custom header is refused (403)" ((Http POST "$($d.Api)/netbt/x?enabled=true") -eq 403)
    Check "CSRF: JSON POST without the custom header is refused (403)" ((Http POST "$($d.Api)/static" @{} '{"name":"X","addresses":["10.1.1.1"]}') -eq 403)
    Check "view only: the server itself cannot change anything without signing in (401)" ((Http POST "$($d.Api)/static" $Csrf '{"name":"NOBODY","addresses":["10.50.9.8"]}') -eq 401)
    Check "view only: nor delete, nor save settings (401)" ((Http DELETE "$($d.Api)/static/SEC-STATIC" $Csrf) -eq 401 -and
        (Http PUT "$($d.Api)/settings" $Csrf '{"Wins:AnswerBroadcasts":"true"}') -eq 401)
    Check "the same change with the admin token is accepted" ((Http POST "$($d.Api)/static" $Admin '{"name":"SEC-STATIC","addresses":["10.50.9.9"]}') -eq 200)
    Check "a static mapping cannot be overwritten by a registration (RCODE 6)" ((Rcode $d (New-Request 5 "SEC-STATIC" "10.50.0.66")) -eq 6)
    Check "oversized request body is refused" ((Http POST "$($d.Api)/static" $Admin ('{"name":"BIG","addresses":["10.1.1.1"],"comment":"' + ("x" * 70000) + '"}')) -ge 400)
    Check "path traversal in a route value changes nothing (404)" ((Http DELETE "$($d.Api)/static/..%2F..%2Fappsettings.json" $Admin) -eq 404)

    # default credentials: admin / admin until changed, and the page is told to insist on a new password
    $me = Invoke-RestMethod "$($d.Api)/me"
    Check "not signed in: not an admin, may not view, and not told that the default password is in use" (
        -not $me.admin -and -not $me.canView -and -not $me.defaultPassword)
    Check "the server's own console must sign in to see data too (401)" ((Http GET "$($d.Api)/names") -eq 401)
    Check "a password cannot be changed without signing in (401)" ((Http POST "$($d.Api)/password" $Csrf '{"currentPassword":"admin","newPassword":"sec-smoke-password"}') -eq 401)
    $web = $null
    $login = Invoke-RestMethod -Method Post "$($d.Api)/login" -Headers $Csrf -ContentType "application/json" -Body '{"username":"admin","password":"admin"}' -SessionVariable web
    Check "a fresh install signs in with admin / admin, flagged as the default password" ($login.admin -and $login.defaultPassword -and
        (Invoke-RestMethod "$($d.Api)/me" -WebSession $web).defaultPassword)
    Check "signed in, the data is visible" ((Invoke-WebRequest "$($d.Api)/names" -WebSession $web -SkipHttpErrorCheck).StatusCode -eq 200)
    function Change-Password([string]$body) {
        (Invoke-WebRequest -Method Post "$($d.Api)/password" -WebSession $web -Headers $Csrf -ContentType "application/json" -SkipHttpErrorCheck -Body $body).StatusCode
    }
    $wrong = Change-Password '{"currentPassword":"nope","newPassword":"sec-smoke-password"}'
    $short = Change-Password '{"currentPassword":"admin","newPassword":"admin"}'
    Check "changing it needs the current password, and admin itself is too short to keep (400, 400)" ($wrong -eq 400 -and $short -eq 400) "$wrong,$short"
    $changed = Change-Password '{"currentPassword":"admin","newPassword":"sec-smoke-password"}'
    Check "the default password can be changed (200), which writes auth.json" ($changed -eq 200 -and (Test-Path (Join-Path $d.Dir "auth.json")))
    Check "afterwards admin / admin no longer works (401)" ((Http POST "$($d.Api)/login" $Csrf '{"username":"admin","password":"admin"}') -eq 401)
    $login = Invoke-RestMethod -Method Post "$($d.Api)/login" -Headers $Csrf -ContentType "application/json" -Body '{"username":"admin","password":"sec-smoke-password"}'
    Check "the new password works and is no longer flagged as the default" ($login.admin -and -not $login.defaultPassword)
    Check "an unauthenticated change is still refused (401)" ((Http POST "$($d.Api)/static" $Csrf '{"name":"NOPE","addresses":["10.1.1.1"]}') -eq 401)
    Check "the replication key cannot be read without signing in (401)" ((Http GET "$($d.Api)/replication/key") -eq 401)
    $codes = 1..6 | ForEach-Object { Http POST "$($d.Api)/login" $Csrf '{"username":"admin","password":"wrong"}' }
    Check "five wrong passwords lock the address out (then 429)" (($codes[0..4] | Where-Object { $_ -eq 401 }).Count -eq 5 -and $codes[5] -eq 429) ($codes -join ",")

    $bdir = Join-Path $root "badauth"
    [void][System.IO.Directory]::CreateDirectory($bdir)
    [System.IO.File]::WriteAllText((Join-Path $bdir "auth.json"), '{ this is not json')
    $b = Start-Node "badauth"
    Check "an unreadable auth.json locks sign-in (no fall-back to admin / admin: 401)" ((Http POST "$($b.Api)/login" $Csrf '{"username":"admin","password":"admin"}') -eq 401)

    # ---------------------------------------------------------------- dashboard languages
    $ldir = Join-Path $root "langnode/lang"
    [void][System.IO.Directory]::CreateDirectory($ldir)
    [System.IO.File]::WriteAllText((Join-Path $ldir "xx.json"), '{ "_code": "xx", "_name": "Test language", "tab.overview": "XX overview" }')
    [System.IO.File]::WriteAllText((Join-Path $ldir "th.json"), '{ "_code": "th", "_name": "Thai, corrected", "tab.overview": "TH fixed" }')
    [System.IO.File]::WriteAllText((Join-Path $ldir "bad.json"), '{ not json')
    $ln = Start-Node "langnode"
    $list = Invoke-RestMethod "$($ln.Api)/lang"
    Check "a language file dropped into <data>/lang is listed and served, no sign-in needed" (
        ($list | Where-Object code -eq "xx").name -eq "Test language" -and (Invoke-RestMethod "$($ln.Api)/lang/xx")."tab.overview" -eq "XX overview")
    Check "a custom file with a built-in code replaces the built-in translation" (
        (Invoke-RestMethod "$($ln.Api)/lang/th")."tab.overview" -eq "TH fixed" -and ($list | Where-Object code -eq "th").source -like "custom*")
    Check "a broken language file is ignored, not served" (-not ($list | Where-Object code -eq "bad") -and (Http GET "$($ln.Api)/lang/bad") -eq 404)
    Check "the language code cannot reach other files (404)" ((Http GET "$($ln.Api)/lang/..%2Fappsettings") -eq 404 -and
        (Http GET "$($ln.Api)/lang/..%2F..%2Fauth") -eq 404 -and (Http GET "$($ln.Api)/lang/EN.json") -eq 404)

    # ---------------------------------------------------------------- flood guards
    Write-Host "`nFlood guards" -ForegroundColor Cyan
    $f = Start-Node "flood" @{ Wins__Security__RateLimitPerSecond = 5; Wins__Security__RateLimitBurst = 20; Wins__Security__MaxNamesPerAddress = 3 }
    $udp = New-Object System.Net.Sockets.UdpClient
    $udp.Client.ReceiveTimeout = 300
    $q = New-Request 0 "NOBODY"
    1..200 | ForEach-Object { [void]$udp.Send($q, $q.Length, "127.0.0.1", $f.Nbns) }
    $answers = 0
    $remote = New-Object System.Net.IPEndPoint ([System.Net.IPAddress]::Any, 0)
    try { while ($true) { [void]$udp.Receive([ref]$remote); $answers++ } } catch { }
    $udp.Close()
    $c = Counters $f
    Check "a 200-packet burst from one address gets about the burst size answered ($answers)" ($answers -ge 15 -and $answers -le 30) "answers=$answers"
    Check "the rest are counted as rate limited ($($c.rateLimited))" ($c.rateLimited -ge 170)

    Start-Sleep -Seconds 5   # let the bucket refill
    $rc = 1..4 | ForEach-Object { $code = Rcode $f (New-Request 5 "QUOTA$_" "10.70.0.1"); Start-Sleep -Milliseconds 300; $code }
    Check "the 4th name for one address breaks the per-address quota (RCODE 5)" (($rc -join ",") -eq "0,0,0,5") ($rc -join ",")

    # the total cap must count names exactly through register / release / re-register cycles
    $t = Start-Node "total" @{ Wins__Security__MaxDynamicNames = 2 }
    $rc = @((Rcode $t (New-Request 5 "CAP-A" "10.71.0.1")), (Rcode $t (New-Request 5 "CAP-B" "10.71.0.2")), (Rcode $t (New-Request 5 "CAP-C" "10.71.0.3")),
            (Rcode $t (New-Request 6 "CAP-A" "10.71.0.1")), (Rcode $t (New-Request 5 "CAP-C" "10.71.0.3")), (Rcode $t (New-Request 5 "CAP-A" "10.71.0.1")),
            (Rcode $t (New-Request 5 "CAP-B" "10.71.0.2")))
    Check "database cap of 2: third name refused, a released slot is reusable, refresh still works" (($rc -join ",") -eq "0,0,5,0,0,5,0") ($rc -join ",")
    Check "the dashboard count agrees (2 dynamic names)" ((Get-Api $t "/status").database.dynamic -eq 2)

    # ---------------------------------------------------------------- registration modes
    Write-Host "`nRegistration modes" -ForegroundColor Cyan
    $s = Start-Node "staticonly" @{ Wins__Security__RegistrationMode = "StaticOnly" }
    [void](Http POST "$($s.Api)/static" $Admin '{"name":"FIXED","addresses":["10.80.0.1"]}')
    Check "StaticOnly: a dynamic registration is refused (RCODE 5)" ((Rcode $s (New-Request 5 "ROGUE" "10.80.0.66")) -eq 5)
    Check "StaticOnly: a host announcing its own static name is acknowledged (RCODE 0)" ((Rcode $s (New-Request 5 "FIXED" "10.80.0.1")) -eq 0)
    Check "StaticOnly: the static name cannot be claimed from another address" ((Rcode $s (New-Request 5 "FIXED" "10.80.0.66")) -eq 5)
    Check "StaticOnly: static names still resolve" ((Rcode $s (New-Request 0 "FIXED")) -eq 0)
    Check "StaticOnly: nothing dynamic was stored" ((Get-Api $s "/status").database.dynamic -eq 0)

    $a = Start-Node "subnets" @{ Wins__Security__RegistrationMode = "AllowedSubnets"; Wins__Security__AllowedSubnets__0 = "127.0.0.0/8"; Wins__Security__AllowedSubnets__1 = "10.50.0.0/16" }
    Check "AllowedSubnets: an address inside an allowed network registers (RCODE 0)" ((Rcode $a (New-Request 5 "INSIDE" "10.50.3.4")) -eq 0)
    Check "AllowedSubnets: an address outside is refused (RCODE 5)" ((Rcode $a (New-Request 5 "OUTSIDE" "10.60.3.4")) -eq 5)
    Check "AllowedSubnets: releasing from outside is refused too" ((Rcode $a (New-Request 6 "INSIDE" "10.60.3.4")) -eq 5)
    Check "AllowedSubnets: queries are not restricted" ((Rcode $a (New-Request 0 "INSIDE")) -eq 0)
    Check "refusals are counted" ((Counters $a).refusedByPolicy -eq 2)

    $m = Start-Node "srcmatch" @{ Wins__Security__RequireAddressMatchesSource = "true" }
    Check "RequireAddressMatchesSource: a host registering its own address is accepted" ((Rcode $m (New-Request 5 "MYSELF" "127.0.0.1")) -eq 0)
    Check "RequireAddressMatchesSource: registering somebody else's address is refused (RCODE 5)" ((Rcode $m (New-Request 5 "SPOOF" "10.50.0.1")) -eq 5)

    # ---------------------------------------------------------------- settings page
    Write-Host "`nSettings from the dashboard" -ForegroundColor Cyan
    $g = Start-Node "settings"
    function Put-Settings($node, [string]$json, $headers = $Admin) { Http PUT "$($node.Api)/settings" $headers $json }
    function Setting($node, [string]$key) { (Get-Api $node "/settings").items | Where-Object key -eq $key }

    $all = Get-Api $g "/settings"
    # Labels and help texts live in the language files: every setting needs both, in every built-in language.
    $langList = Invoke-RestMethod "$($g.Api)/lang"
    Check "the built-in languages are listed without signing in (en, th)" ((@($langList.code) -contains "en") -and (@($langList.code) -contains "th"))
    foreach ($code in "en", "th") {
        $texts = Invoke-RestMethod "$($g.Api)/lang/$code"
        $missing = @($all.items | Where-Object { -not $texts."setting.$($_.key).label" -or -not $texts."setting.$($_.key).help" } | ForEach-Object key)
        Check "every setting has a label and an explanation in $code ($($all.items.Count) settings)" ($all.items.Count -ge 30 -and $missing.Count -eq 0) ($missing -join ", ")
    }
    $tokenRow = $all.items | Where-Object key -eq "Dashboard:AdminToken"
    Check "the API token is never sent back, only whether one is set" ($tokenRow.isSet -and $tokenRow.value -eq "" -and $tokenRow.running -eq "")

    Check "a blocked name added in the dashboard is refused at once, no restart (RCODE 5)" ((Rcode $g (New-Request 5 "EVILNAME" "10.90.0.1")) -eq 0 -and
        (Put-Settings $g '{"Wins:Security:BlockedNames":"WPAD, ISATAP, EVILNAME"}') -eq 200 -and (Rcode $g (New-Request 5 "EVILNAME2" "10.90.0.2")) -eq 0 -and
        (Rcode $g (New-Request 5 "EVILNAME" "10.90.0.3")) -eq 5)
    Check "AllowedSubnets with an empty subnet list is refused (would lock everyone out)" ((Put-Settings $g '{"Wins:Security:RegistrationMode":"AllowedSubnets"}') -eq 400)
    Check "mode + subnets saved together take effect immediately" ((Put-Settings $g '{"Wins:Security:RegistrationMode":"AllowedSubnets","Wins:Security:AllowedSubnets":"127.0.0.0/8, 10.90.0.0/24"}') -eq 200 -and
        (Rcode $g (New-Request 5 "IN-NET" "10.90.0.9")) -eq 0 -and (Rcode $g (New-Request 5 "OUT-NET" "10.91.0.9")) -eq 5)
    Check "and can be switched back" ((Put-Settings $g '{"Wins:Security:RegistrationMode":"Dynamic"}') -eq 200 -and (Rcode $g (New-Request 5 "OUT-NET" "10.91.0.9")) -eq 0)
    Check "the per-address quota follows the setting without a restart" ((Put-Settings $g '{"Wins:Security:MaxNamesPerAddress":"1"}') -eq 200 -and
        (Rcode $g (New-Request 5 "Q-ONE" "10.92.0.1")) -eq 0 -and (Rcode $g (New-Request 5 "Q-TWO" "10.92.0.1")) -eq 5)

    Check "a key outside the catalogue cannot be written (data folder, dashboard address)" ((Put-Settings $g '{"Wins:DataDirectory":"C:\\\\Windows"}') -eq 400 -and
        (Put-Settings $g '{"Kestrel:Endpoints:Management:Url":"http://0.0.0.0:1"}') -eq 400)
    Check "out-of-range and malformed values are refused, not clamped" ((Put-Settings $g '{"Wins:MaxTtlSeconds":"5"}') -eq 400 -and
        (Put-Settings $g '{"Wins:Dns:Servers":"not-an-ip"}') -eq 400 -and (Put-Settings $g '{"Wins:SelfSuffixes":"00,1C"}') -eq 400 -and
        (Put-Settings $g '{"Wins:MinTtlSeconds":"999999999"}') -eq 400)
    Check "one bad value saves nothing from the same request" ((Put-Settings $g '{"Wins:AnswerBroadcasts":"true","Wins:Port":"99999"}') -eq 400 -and
        (Setting $g "Wins:AnswerBroadcasts").value -eq "false")
    Check "a setting fixed by an environment variable is reported, not silently ignored" ((Put-Settings $g '{"Wins:Port":"137"}') -eq 400)

    Check "a restart-only setting is saved and reported as waiting for a restart" ((Put-Settings $g '{"Wins:QueryLogCapacity":"500"}') -eq 200)
    $row = Setting $g "Wins:QueryLogCapacity"
    Check "  value 500, still running 1000, listed under pending" ($row.value -eq "500" -and $row.running -eq "1000" -and $row.pending -and
        (Get-Api $g "/settings").pendingRestart -contains "Wins:QueryLogCapacity")

    Check "a list can be emptied (overriding the list in appsettings.json): WPAD registers" ((Put-Settings $g '{"Wins:Security:BlockedNames":""}') -eq 200 -and
        (Setting $g "Wins:Security:BlockedNames").value -eq "" -and (Rcode $g (New-Request 5 "WPAD" "10.93.0.1")) -eq 0)
    Check "and filled again" ((Put-Settings $g '{"Wins:Security:BlockedNames":"WPAD, ISATAP, EVILNAME","Wins:AnswerBroadcasts":"true"}') -eq 200)
    Check "appsettings.json next to the exe is not touched; the changes are in settings.json in the data folder" (Test-Path (Join-Path $g.Dir "settings.json"))

    # the same data folder after a restart: everything saved is in effect
    Stop-Process -Id $g.Process.Id -Force; Start-Sleep -Milliseconds 500
    $g2 = Start-Node "settings"
    $row = Setting $g2 "Wins:QueryLogCapacity"
    Check "after a restart the saved values are the running ones" ($row.running -eq "500" -and -not $row.pending -and
        @((Get-Api $g2 "/settings").pendingRestart).Count -eq 0 -and (Setting $g2 "Wins:AnswerBroadcasts").running -eq "true")
    Check "after a restart the blocked name is still blocked (RCODE 5)" ((Rcode $g2 (New-Request 5 "EVILNAME" "10.90.0.3")) -eq 5)

    # Windows Firewall rules (nothing is added here: that would change the machine running the test)
    $fw = Get-Api $g2 "/firewall"
    if ($fw.firewall -eq "none") {
        # Linux without firewalld / ufw (e.g. the WSL dev box): nothing to manage, and the page says so.
        Check "no firewall manager: reported as such, no rules listed" (-not $fw.supported -and @($fw.rules).Count -eq 0)
    }
    else {
    Check "the firewall page lists the three ports this server needs ($(@($fw.rules).Count)) - $($fw.firewall)" ($fw.supported -and @($fw.rules).Count -eq 3 -and
        ($fw.rules | Where-Object id -eq "nbns").port -eq $g2.Nbns -and ($fw.rules | Where-Object id -eq "dashboard").port -eq $g2.Dash)
    Check "changing a firewall rule needs a sign-in (401), and only the known rules exist (400)" ((Http POST "$($g2.Api)/firewall/dashboard?allow=true" $Csrf) -eq 401 -and
        (Http POST "$($g2.Api)/firewall/evil%22%20dir=out?allow=true" $Admin) -eq 400)
    }

    # restart from the dashboard (a real restart needs the installed service; here: the guards around it)
    Check "restarting the service needs a sign-in (401)" ((Http POST "$($g2.Api)/service/restart" $Csrf) -eq 401)
    Check "an instance started from a console says it cannot be restarted (400, canRestart false)" ((Http POST "$($g2.Api)/service/restart" $Admin) -eq 400 -and
        -not (Get-Api $g2 "/settings").canRestart -and (Invoke-RestMethod "$($g2.Api)/info").startedAt -gt 0)
    Check "  and it is still running" ((Http GET "$($g2.Api)/info") -eq 200)

    # the API token itself: this node gets it from settings.json, not from the environment
    $tdir = Join-Path $root "token"
    [void][System.IO.Directory]::CreateDirectory($tdir)
    [System.IO.File]::WriteAllText((Join-Path $tdir "settings.json"), '{"Dashboard:AdminToken":"seeded-token-0123456789"}')
    $k = Start-Node "token" @{ Dashboard__AdminToken = "" }
    $seeded = @{ "X-Requested-With" = "WinsAlt"; "X-Admin-Token" = "seeded-token-0123456789" }
    $renewed = @{ "X-Requested-With" = "WinsAlt"; "X-Admin-Token" = "a-brand-new-token-123" }
    Check "settings.json is read at startup (its token is the admin token)" ((Put-Settings $k '{"Wins:AnswerBroadcasts":"true"}' $seeded) -eq 200)
    Check "a token shorter than 16 characters is refused" ((Put-Settings $k '{"Dashboard:AdminToken":"short"}' $seeded) -eq 400)
    Check "replacing the API token takes effect at once (old one 401, new one 200)" ((Put-Settings $k '{"Dashboard:AdminToken":"a-brand-new-token-123"}' $seeded) -eq 200 -and
        (Put-Settings $k '{"Wins:AnswerBroadcasts":"false"}' $seeded) -eq 401 -and (Put-Settings $k '{"Wins:AnswerBroadcasts":"false"}' $renewed) -eq 200)
    Check "removing the token leaves nobody able to change anything until a password is set" ((Put-Settings $k '{"Dashboard:AdminToken":""}' $renewed) -eq 200 -and
        (Put-Settings $k '{"Wins:AnswerBroadcasts":"true"}' $renewed) -eq 401)
    Check "on a node whose token comes from the environment, changing it is refused (it would do nothing)" ((Put-Settings $g2 '{"Dashboard:AdminToken":"a-brand-new-token-123"}') -eq 400)

    # ---------------------------------------------------------------- sign-in to view
    Write-Host "`nSign-in to view" -ForegroundColor Cyan
    # (Debian maps its own host name to 127.0.1.1, so ask the adapters rather than DNS.)
    $lan = [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() |
        Where-Object { $_.OperationalStatus -eq "Up" } | ForEach-Object { $_.GetIPProperties().UnicastAddresses.Address } |
        Where-Object { $_.AddressFamily -eq "InterNetwork" -and -not [System.Net.IPAddress]::IsLoopback($_) } | Select-Object -First 1
    if ($lan) {
        $v = $d   # the default configuration
        Check "by default another machine's address gets 401 for data" ((Http GET "http://$($lan):$($v.Dash)/api/names") -eq 401)
        Check "the sign-in endpoints stay reachable" ((Http GET "http://$($lan):$($v.Dash)/api/me") -eq 200)
        Check "/api/me tells the page not to poll data (canView false), from the LAN and from the server itself" (
            -not (Invoke-RestMethod "http://$($lan):$($v.Dash)/api/me").canView -and -not (Invoke-RestMethod "$($v.Api)/me").canView)
        Check "with the token (or a sign-in) the data is there, from either address" ((Http GET "http://$($lan):$($v.Dash)/api/names" $Admin) -eq 200 -and
            (Http GET "$($v.Api)/names" $Admin) -eq 200)
        $ov = Start-Node "openview" @{ Dashboard__RequireSignInToView = "false" }
        $open = Http GET "http://$($lan):$($ov.Dash)/api/names"
        Check "with the option switched off, anyone may look (but not change)" ($open -eq 200 -and (Invoke-RestMethod "$($ov.Api)/me").canView -and
            (Http POST "$($ov.Api)/static" $Csrf '{"name":"NOPE","addresses":["10.1.1.1"]}') -eq 401) "status=$open"

        # "Open the dashboard to other computers": the switch decides the listen address, the port stays
        Write-Host "`nDashboard reachable from other computers" -ForegroundColor Cyan
        function Reachable([string]$url) { try { [void](Invoke-WebRequest $url -TimeoutSec 3); $true } catch { $false } }
        $o = Start-Node "remote" @{ Kestrel__Endpoints__Management__Url = "http://127.0.0.1:$(18160 + $script:nextPort + 1)" }
        Check "bound to 127.0.0.1: not reachable on the LAN address, switch shows off" (-not (Reachable "http://$($lan):$($o.Dash)/api/info") -and
            (Setting $o "Dashboard:RemoteAccess").running -eq "false")
        Check "switching it on is saved and waits for a restart" ((Put-Settings $o '{"Dashboard:RemoteAccess":"true"}') -eq 200 -and
            (Setting $o "Dashboard:RemoteAccess").pending -and -not (Reachable "http://$($lan):$($o.Dash)/api/info"))
        Stop-Process -Id $o.Process.Id -Force; Start-Sleep -Milliseconds 500
        $o2 = Start-Node "remote" @{ Kestrel__Endpoints__Management__Url = "http://127.0.0.1:$(18160 + $script:nextPort + 1)" }
        Check "after the restart the same port answers on the LAN address" ((Reachable "http://$($lan):$($o2.Dash)/api/info") -and
            (Setting $o2 "Dashboard:RemoteAccess").running -eq "true")
        Check "and it can be closed again (off + restart = loopback only)" ((Put-Settings $o2 '{"Dashboard:RemoteAccess":"false"}') -eq 200)
        Stop-Process -Id $o2.Process.Id -Force; Start-Sleep -Milliseconds 500
        $o3 = Start-Node "remote" @{ Kestrel__Endpoints__Management__Url = "http://0.0.0.0:$(18160 + $script:nextPort + 1)" }
        Check "  even when the configured address was 0.0.0.0" (-not (Reachable "http://$($lan):$($o3.Dash)/api/info") -and (Reachable "$($o3.Api)/info"))
    }
    else { Write-Host "  SKIP  no non-loopback IPv4 address on this machine" -ForegroundColor Yellow }
}
finally {
    foreach ($node in $script:nodes) { if ($node.Process) { Stop-Process -Id $node.Process.Id -Force -ErrorAction SilentlyContinue } }
    Start-Sleep -Milliseconds 400
    try { [System.IO.Directory]::Delete($root, $true) } catch { }
}

if ($script:failed -gt 0) { Write-Host "`n$($script:failed) check(s) failed." -ForegroundColor Red; exit 1 }
Write-Host "`nAll checks passed." -ForegroundColor Green
