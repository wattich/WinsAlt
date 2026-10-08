# =============================================================================
# replication-smoke.ps1 - end-to-end check of WinsAlt-to-WinsAlt replication.
#
# Starts TWO WinsAlt instances (A and B) on loopback test ports with throw-away
# data folders, links them as replication peers, and checks over real NBNS
# packets that each one answers for names registered on the other - and stops
# doing so when the key is wrong or the name is released at its owner.
#
#   pwsh tools\replication-smoke.ps1                      # uses publish\win-x64\WinsAlt.exe
#   pwsh tools\replication-smoke.ps1 -Exe path\WinsAlt.exe
# =============================================================================
param(
    # Windows: publish\win-x64\WinsAlt.exe; Linux (pwsh): publish/linux-x64/winsalt
    [string]$Exe = (Join-Path $PSScriptRoot $(if ($IsLinux) { "../publish/linux-x64/winsalt" } else { "../publish/win-x64/WinsAlt.exe" }))
)

$ErrorActionPreference = "Stop"
$script:failed = 0
$script:trn = 0x5000

$A = @{ Name = "A"; Nbns = 11141; Dash = 18141; Repl = 18151 }
$B = @{ Name = "B"; Nbns = 11142; Dash = 18142; Repl = 18152 }
$AdminToken = "smoke-test-admin-token"
$root = Join-Path ([System.IO.Path]::GetTempPath()) ("winsalt-repl-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
# Start-Process arguments for one instance: hidden window on Windows; on Linux the output goes to
# files in the instance's folder instead of this console (-WindowStyle does not exist there).
function Start-Arguments([string]$dir) {
    $a = @{ FilePath = $Exe; PassThru = $true }
    if ($IsLinux) { $a.RedirectStandardOutput = Join-Path $dir "stdout.log"; $a.RedirectStandardError = Join-Path $dir "stderr.log" }
    else { $a.WindowStyle = "Hidden" }
    return $a
}

function Start-Node($node) {
    $dir = Join-Path $root $node.Name
    New-Item -ItemType Directory -Force $dir | Out-Null
    $env:Wins__Port = $node.Nbns
    $env:Wins__ListenAddress = "127.0.0.1"
    $env:Wins__DataDirectory = $dir
    $env:Wins__ReplicationPort = $node.Repl
    $env:Wins__RegisterSelf = "false"
    $env:Wins__Dns__Enabled = "false"
    $env:Kestrel__Endpoints__Management__Url = "http://127.0.0.1:$($node.Dash)"
    $env:Dashboard__AdminToken = $AdminToken   # nothing can be changed through the API without signing in
    $startArgs = Start-Arguments $dir
    $node.Process = Start-Process @startArgs
    $node.Api = "http://127.0.0.1:$($node.Dash)/api"
}

function Api($node, [string]$method, [string]$path, $body = $null) {
    $args = @{ Method = $method; Uri = $node.Api + $path; Headers = @{ "X-Requested-With" = "WinsAlt"; "X-Admin-Token" = $AdminToken } }
    if ($null -ne $body) { $args.ContentType = "application/json"; $args.Body = ($body | ConvertTo-Json -Compress) }
    Invoke-RestMethod @args
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

# opcode 0 = query, 5 = registration, 6 = release. Returns @{ Rcode; Address } or $null.
function Nbns($node, [int]$opcode, [string]$name, [string]$ip = "") {
    $script:trn++
    $ar = if ($ip) { 1 } else { 0 }
    $pkt = (U16 $script:trn) + (U16 (($opcode -shl 11) -bor 0x0100)) + (U16 1) + (U16 0) + (U16 0) + (U16 $ar)
    $pkt += (Encode-Name $name 0x20) + (U16 0x20) + (U16 1)
    if ($ip) {
        $pkt += [byte[]]@(0xC0, 0x0C) + (U16 0x20) + (U16 1) + [byte[]]@(0, 4, 0x93, 0xE0) + (U16 6) + (U16 0x6000) + [System.Net.IPAddress]::Parse($ip).GetAddressBytes()
    }
    $udp = New-Object System.Net.Sockets.UdpClient
    try {
        $udp.Client.ReceiveTimeout = 4000
        [void]$udp.Send([byte[]]$pkt, $pkt.Length, "127.0.0.1", $node.Nbns)
        $remote = New-Object System.Net.IPEndPoint ([System.Net.IPAddress]::Any, 0)
        try { $r = $udp.Receive([ref]$remote) } catch { return $null }
        $address = if ($r.Length -ge 62) { "{0}.{1}.{2}.{3}" -f $r[58], $r[59], $r[60], $r[61] } else { "" }
        return @{ Rcode = ($r[3] -band 0x0F); Address = $address }
    }
    finally { $udp.Close() }
}

function Check([string]$what, [bool]$ok, $detail = "") {
    if ($ok) { Write-Host "  PASS  $what" -ForegroundColor Green }
    else { Write-Host "  FAIL  $what  $detail" -ForegroundColor Red; $script:failed++ }
}

# Polls until the condition holds (replication is asynchronous) or the timeout passes.
function Wait-Until([scriptblock]$condition, [int]$seconds = 15) {
    for ($i = 0; $i -lt $seconds * 4; $i++) { if (& $condition) { return $true }; Start-Sleep -Milliseconds 250 }
    return $false
}

function Peer($node) { (Api $node GET "/replication").peers | Select-Object -First 1 }

try {
    Write-Host "Starting two WinsAlt instances from $Exe" -ForegroundColor Cyan
    # A starts with a registration restored from a wins-db.json written before 1.7.0 (no refreshedAt): the
    # record a server keeps after its client moved to another server - the case "the newest claim wins" is for.
    $legacy = "OLDMOVER" + (Get-Random -Maximum 999)
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $legacyHex = [Convert]::ToHexString([System.Text.Encoding]::ASCII.GetBytes($legacy.PadRight(15)) + [byte[]]@(0x20))
    New-Item -ItemType Directory -Force (Join-Path $root "A") | Out-Null
    # And one the way a real server held clients that had moved: multihomed with the client's old AND new address.
    $stale = "STALEMH" + (Get-Random -Maximum 999)
    $staleHex = [Convert]::ToHexString([System.Text.Encoding]::ASCII.GetBytes($stale.PadRight(15)) + [byte[]]@(0x20))
    # The 1.7.1 regression: the client refreshes its new address at BOTH servers (primary + secondary WINS), so A's
    # record is the fresher one - but it still carries the old address. Only the old address may go, at A; B keeps.
    $both = "BOTHWINS" + (Get-Random -Maximum 999)
    $bothHex = [Convert]::ToHexString([System.Text.Encoding]::ASCII.GetBytes($both.PadRight(15)) + [byte[]]@(0x20))
    $seed = @{ version = 1; savedAt = $now; records = @(
               @{ name = $legacyHex; kind = "Unique"; flags = 0x6000; registeredAt = $now - 3600
                  members = @(@{ ip = "10.50.0.7"; expiresAt = $now + 200000 }) },
               @{ name = $staleHex; kind = "Multihomed"; flags = 0x6000; registeredAt = $now - 3600
                  members = @(@{ ip = "10.50.0.30"; expiresAt = $now + 150000 }, @{ ip = "10.60.0.30"; expiresAt = $now + 200000 }) },
               @{ name = $bothHex; kind = "Multihomed"; flags = 0x6000; registeredAt = $now - 3600
                  members = @(@{ ip = "10.50.0.40"; expiresAt = $now + 150000 }, @{ ip = "10.60.0.40"; expiresAt = $now + 350000 }) }) }
    [System.IO.File]::WriteAllText((Join-Path $root "A/wins-db.json"), ($seed | ConvertTo-Json -Depth 5))
    Start-Node $A; Start-Node $B
    [void](Wait-Until { try { (Api $A GET "/status") -and (Api $B GET "/status") } catch { $false } })

    $ha = "HOSTA" + (Get-Random -Maximum 9999); $hb = "HOSTB" + (Get-Random -Maximum 9999)
    [void](Nbns $A 5 $ha "10.50.0.1")
    [void](Nbns $B 5 $hb "10.60.0.1")
    Check "before linking: B does not know A's name" ((Nbns $B 0 $ha).Rcode -eq 3)

    # Static-first precedence across servers: A holds SHARED as a static mapping, while a machine
    # at site B has (already) registered the same name dynamically.
    $shared = "SHARED" + (Get-Random -Maximum 9999)
    [void](Api $A POST "/static" @{ name = $shared; addresses = @("10.50.9.9"); suffixes = @("20") })
    [void](Nbns $B 5 $shared "10.60.9.9")
    Check "before linking: B answers SHARED from its own registration" ((Nbns $B 0 $shared).Address -eq "10.60.9.9")

    $key = "smoke-test-replication-key"
    [void](Api $A POST "/replication/key" @{ key = $key }); [void](Api $B POST "/replication/key" @{ key = $key })
    [void](Api $A POST "/replication/peers" @{ name = "site B"; address = "127.0.0.1"; port = $B.Repl; enabled = $true })
    [void](Api $B POST "/replication/peers" @{ name = "site A"; address = "127.0.0.1"; port = $A.Repl; enabled = $true })

    # A server only answers the servers on its own list, so A's first pull (made before B listed A) is
    # refused; it succeeds on A's next 30-second round.
    Check "a pull is refused until the other side lists this server too" (Wait-Until { (Peer $A).lastError -or (Peer $A).lastSyncAt -gt 0 } 10)
    Check "both servers report the link in sync" (Wait-Until { (Peer $A).lastSyncAt -gt 0 -and (Peer $B).lastSyncAt -gt 0 } 45) ((Peer $A) | Out-String)

    $r = Nbns $B 0 $ha
    Check "B answers for a name registered on A" ($r -and $r.Rcode -eq 0 -and $r.Address -eq "10.50.0.1") ($r | Out-String)
    $r = Nbns $A 0 $hb
    Check "A answers for a name registered on B" ($r -and $r.Rcode -eq 0 -and $r.Address -eq "10.60.0.1") ($r | Out-String)

    $r = Nbns $B 0 $shared
    Check "a static mapping on A outranks the registration on B" ($r -and $r.Address -eq "10.50.9.9") ($r | Out-String)
    Check "B refuses a new claim on a name that is static on A (RCODE 6)" ((Nbns $B 5 $shared "10.60.9.77").Rcode -eq 6)
    Check "the host the static mapping names is still acknowledged at B (RCODE 0)" ((Nbns $B 5 $shared "10.50.9.9").Rcode -eq 0)
    $rows = (Api $B GET "/names").items | Where-Object name -eq $shared
    Check "B's name list shows it once, as the static replica" (@($rows).Count -eq 1 -and $rows.source -eq "Replication" -and $rows.isStatic) ($rows | Out-String)

    $row = (Api $B GET "/names?type=replica").items | Where-Object name -eq $ha
    Check "dashboard lists it on B with source Replication, from 'site A'" ($row -and $row.source -eq "Replication" -and $row.origin -eq "site A") ($row | Out-String)
    $local = (Api $B GET "/names?source=local").items
    Check "source filter: B's own name is Local, A's name is not in the Local list" (($local | Where-Object name -eq $hb).source -eq "Local" -and -not ($local | Where-Object name -eq $ha)) ($local | Out-String)
    Check "A's own names are not echoed back to it as replicas" (-not ((Api $A GET "/names?type=replica").items | Where-Object name -eq $ha))

    # A name another server owns is not free for the taking: the owner's address is challenged
    # first (nobody answers at 10.50.0.1 here, so after the challenge the newcomer gets it).
    [void](Nbns $B 5 $ha "10.60.0.99")
    $challenged = (Api $B GET "/querylog?limit=50") | Where-Object { $_.name -eq $ha -and $_.result -eq "Challenging" -and $_.answer -eq "10.50.0.1" }
    Check "claiming a replicated name challenges the owner the replica names" ([bool]$challenged)
    Check "an undefended replicated name goes to the local claimant" (Wait-Until { (Nbns $B 0 $ha).Address -eq "10.60.0.99" } 6)
    [void](Nbns $B 6 $ha "10.60.0.99")

    # Replication must never remove or shorten a local record (1.7.1 and 1.7.2 did, and were withdrawn): clients
    # refresh at both servers, and some hosts register different adapters with different servers.
    $mover = "MOVER" + (Get-Random -Maximum 9999); $same = "SAMEHOST" + (Get-Random -Maximum 999)
    [void](Nbns $A 5 $mover "10.50.0.5")
    [void](Nbns $A 5 $same "10.50.0.8")
    Check "A's registrations reach B" (Wait-Until { (Nbns $B 0 $mover).Address -eq "10.50.0.5" -and (Nbns $B 0 $same).Address -eq "10.50.0.8" } 45)
    [void](Nbns $B 5 $mover "10.60.0.5")      # challenges 10.50.0.5, nobody answers, granted
    [void](Nbns $B 5 $same "10.50.0.8")       # same host, same address: no conflict
    [void](Nbns $B 5 $legacy "10.60.0.7")     # A's pre-1.7.0 record: compared by expiry (300000 s here > 200000 s there)
    [void](Nbns $B 5 $stale "10.60.0.30")     # the new address only (it is one of A's, so no challenge)
    [void](Nbns $B 5 $both "10.60.0.40")      # same, but A's copy of the new address is the fresher one
    Check "the moved client's new address is answered at B" (Wait-Until { (Nbns $B 0 $mover).Address -eq "10.60.0.5" } 10)
    function LocalAddresses($node, [string]$name) { (((Api $node GET "/names?source=local").items | Where-Object name -eq $name).addresses | Sort-Object) -join "," }
    # Let both sides pull each other's new records at least once (a pull every 30 s).
    $syncA = (Peer $A).lastSyncAt; $syncB = (Peer $B).lastSyncAt
    [void](Wait-Until { (Peer $A).lastSyncAt -gt $syncA -and (Peer $B).lastSyncAt -gt $syncB } 45)
    [void](Wait-Until { (Peer $A).lastSyncAt -gt $syncA + 1 -and (Peer $B).lastSyncAt -gt $syncB + 1 } 45)
    Check "replication never removes a local record or address: A keeps every one of its own" (
        (LocalAddresses $A $mover) -eq "10.50.0.5" -and (LocalAddresses $A $legacy) -eq "10.50.0.7" -and
        (LocalAddresses $A $stale) -eq "10.50.0.30,10.60.0.30" -and (LocalAddresses $A $both) -eq "10.50.0.40,10.60.0.40") (
        "mover=$(LocalAddresses $A $mover) legacy=$(LocalAddresses $A $legacy) stale=$(LocalAddresses $A $stale) both=$(LocalAddresses $A $both)")
    Check "  and B keeps every one of its own" ((LocalAddresses $B $mover) -eq "10.60.0.5" -and (LocalAddresses $B $legacy) -eq "10.60.0.7" -and
        (LocalAddresses $B $stale) -eq "10.60.0.30" -and (LocalAddresses $B $both) -eq "10.60.0.40")
    Check "the same host registered at both servers (same address) is kept at both" (
        [bool]((Api $A GET "/names?source=local").items | Where-Object name -eq $same) -and [bool]((Api $B GET "/names?source=local").items | Where-Object name -eq $same))

    # Wrong key on B: A must refuse, B must say why.
    [void](Api $B POST "/replication/key" @{ key = "a-different-key-entirely" })
    Check "wrong key: B reports the link as failed" (Wait-Until { (Peer $B).lastError }) ((Peer $B) | Out-String)
    Check "wrong key: A rejected the pull" (Wait-Until { (Api $A GET "/logs") | Where-Object message -like "*wrong replication key*" })

    # Back to the right key; a name released on A disappears from B with the next copy.
    [void](Nbns $A 6 $ha "10.50.0.1")
    [void](Api $B POST "/replication/key" @{ key = $key })
    Check "right key again: link recovers" (Wait-Until { -not (Peer $B).lastError })
    Check "a name released on A is gone from B" (Wait-Until { (Nbns $B 0 $ha).Rcode -eq 3 })

    # Removing the peer drops everything copied from it.
    [void](Api $B DELETE "/replication/peers/127.0.0.1")
    Check "removing the peer removes its replicas" (Wait-Until { (Api $B GET "/status").database.replicas -eq 0 })
}
finally {
    foreach ($node in $A, $B) { if ($node.Process) { Stop-Process -Id $node.Process.Id -Force -ErrorAction SilentlyContinue } }
    Start-Sleep -Milliseconds 300
    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failed -gt 0) { Write-Host "`n$($script:failed) check(s) failed." -ForegroundColor Red; exit 1 }
Write-Host "`nAll checks passed." -ForegroundColor Green
