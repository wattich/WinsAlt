# =============================================================================
# nbns-smoke.ps1 - end-to-end protocol check against a running WinsAlt instance.
#
# Speaks real NBNS (RFC 1002) packets over UDP: register -> query -> refresh ->
# conflict -> release -> negative query, and checks every reply byte-for-byte
# where it matters (flags, RCODE, address). Exits non-zero on the first failure.
#
#   dev instance:   $env:Wins__Port=11137; $env:Wins__ListenAddress='127.0.0.1'
#                   $env:Dashboard__AdminToken='smoke-test-admin-token'; dotnet WinsAlt.dll
#   then:           pwsh tools\nbns-smoke.ps1 -Port 11137
#
# -Policy must match the server's Wins:ConflictPolicy. With Challenge (the default)
# the server holds the newcomer with a WACK, queries the owner (10.99.0.1 - nobody
# there, so the name is not defended) and then grants it; with Reject it answers
# ACT_ERR straight away.
# =============================================================================
param(
    [string]$Server = "127.0.0.1",
    [int]$Port = 137,
    [ValidateSet("Challenge", "Reject")][string]$Policy = "Challenge",
    # Set to the server's Wins:ChallengePort (e.g. 13700) to also run the multihomed-machine test.
    [int]$ChallengePort = 0,
    # Dashboard URL of the server under test (e.g. http://127.0.0.1:18137) to also run the partner-server test.
    [string]$Dashboard = "",
    # The server's Dashboard:AdminToken. Nothing can be changed through the API without signing in,
    # so the server under test must be started with the same value (Dashboard__AdminToken).
    [string]$AdminToken = "smoke-test-admin-token"
)

$ErrorActionPreference = "Stop"
$script:failed = 0
$script:trn = 0x4000
$Csrf = @{ "X-Requested-With" = "WinsAlt"; "X-Admin-Token" = $AdminToken }   # every change through the API must carry both

function Encode-Name([string]$name, [byte]$suffix) {
    $raw = [System.Text.Encoding]::ASCII.GetBytes($name.ToUpperInvariant().PadRight(15)) + @($suffix)
    $out = New-Object System.Collections.Generic.List[byte]
    $out.Add(0x20)
    foreach ($b in $raw) { $out.Add([byte](0x41 + ($b -shr 4))); $out.Add([byte](0x41 + ($b -band 0x0F))) }
    $out.Add(0)
    return ,$out.ToArray()
}

function U16([int]$v) { return [byte[]]@((($v -shr 8) -band 0xFF), ($v -band 0xFF)) }
function U32([long]$v) { return [byte[]]@((($v -shr 24) -band 0xFF), (($v -shr 16) -band 0xFF), (($v -shr 8) -band 0xFF), ($v -band 0xFF)) }

# opcode: 0 query, 5 registration, 6 release, 8 refresh. $ip empty => question only.
function New-Packet([int]$opcode, [string]$name, [byte]$suffix, [string]$ip, [int]$nbFlags = 0, [int]$ttl = 300000, [int]$nm = 0x0100) {
    $script:trn++
    $flags = ($opcode -shl 11) -bor $nm
    $ar = if ($ip) { 1 } else { 0 }
    $pkt = (U16 $script:trn) + (U16 $flags) + (U16 1) + (U16 0) + (U16 0) + (U16 $ar)
    $pkt += (Encode-Name $name $suffix) + (U16 0x20) + (U16 1)
    if ($ip) {
        $addr = [System.Net.IPAddress]::Parse($ip).GetAddressBytes()
        $pkt += [byte[]]@(0xC0, 0x0C) + (U16 0x20) + (U16 1) + (U32 $ttl) + (U16 6) + (U16 $nbFlags) + $addr
    }
    return ,[byte[]]$pkt
}

# Sends one request and returns the parsed replies ($replies of them; $null for a missing one).
function Send-Packet([byte[]]$pkt, [int]$timeoutMs = 4000, [int]$replies = 1) {
    $udp = New-Object System.Net.Sockets.UdpClient
    try {
        $udp.Client.ReceiveTimeout = $timeoutMs
        [void]$udp.Send($pkt, $pkt.Length, $Server, $Port)
        if ($replies -eq 1) { return Read-Reply $udp }
        $all = @()
        for ($n = 0; $n -lt $replies; $n++) { $all += ,(Read-Reply $udp) }
        return ,$all
    }
    finally { $udp.Close() }
}

function Read-Reply($udp) {
        $remote = New-Object System.Net.IPEndPoint ([System.Net.IPAddress]::Any, 0)
        try { $reply = $udp.Receive([ref]$remote) } catch { return $null }
        $flags = ([int]$reply[2] -shl 8) -bor $reply[3]
        $result = [pscustomobject]@{
            Trn      = ([int]$reply[0] -shl 8) -bor $reply[1]
            Response = ($flags -band 0x8000) -ne 0
            Opcode   = ($flags -shr 11) -band 0x0F
            Rcode    = $flags -band 0x0F
            AnCount  = ([int]$reply[6] -shl 8) -bor $reply[7]
            Type     = 0
            Ttl      = 0
            Addresses = @()
            Length   = $reply.Length
        }
        # record: name(34) type(2) class(2) ttl(4) rdlength(2) rdata
        if ($reply.Length -ge 12 + 34 + 10) {
            $p = 12 + 34
            $result.Type = ([int]$reply[$p] -shl 8) -bor $reply[$p + 1]
            $result.Ttl = ([long]$reply[$p + 4] -shl 24) -bor ([int]$reply[$p + 5] -shl 16) -bor ([int]$reply[$p + 6] -shl 8) -bor $reply[$p + 7]
            $rdlen = ([int]$reply[$p + 8] -shl 8) -bor $reply[$p + 9]
            $p += 10
            for ($i = 0; $i + 6 -le $rdlen; $i += 6) {
                $result.Addresses += "{0}.{1}.{2}.{3}" -f $reply[$p + $i + 2], $reply[$p + $i + 3], $reply[$p + $i + 4], $reply[$p + $i + 5]
            }
        }
        return $result
}

function Check([string]$what, [bool]$ok, $detail = "") {
    if ($ok) { Write-Host "  PASS  $what" -ForegroundColor Green }
    else { Write-Host "  FAIL  $what  $detail" -ForegroundColor Red; $script:failed++ }
}

$host1 = "SMOKE" + (Get-Random -Maximum 99999)
Write-Host "NBNS smoke test against ${Server}:${Port} (name $host1)" -ForegroundColor Cyan

$r = Send-Packet (New-Packet 0 $host1 0x20 "")
Check "query unknown name -> negative (NAM_ERR)" ($r -and $r.Response -and $r.Rcode -eq 3 -and $r.Type -eq 0x0A) ($r | Out-String)

$r = Send-Packet (New-Packet 5 $host1 0x20 "10.99.0.1")
Check "register unique -> positive, opcode 5" ($r -and $r.Response -and $r.Opcode -eq 5 -and $r.Rcode -eq 0 -and $r.Addresses[0] -eq "10.99.0.1") ($r | Out-String)
Check "granted TTL = requested 300000s (inside the 6h..6d window)" ($r -and $r.Ttl -eq 300000) "ttl=$($r.Ttl)"

$r = Send-Packet (New-Packet 0 $host1 0x20 "")
Check "query registered name -> 10.99.0.1" ($r -and $r.Rcode -eq 0 -and $r.AnCount -eq 1 -and $r.Type -eq 0x20 -and $r.Addresses[0] -eq "10.99.0.1") ($r | Out-String)

$r = Send-Packet (New-Packet 0 $host1.ToLowerInvariant() 0x00 "")
Check "other suffix (00) is a different name -> negative" ($r -and $r.Rcode -eq 3) ($r | Out-String)

$r = Send-Packet (New-Packet 8 $host1 0x20 "10.99.0.1")
Check "refresh by owner -> positive, opcode 8" ($r -and $r.Opcode -eq 8 -and $r.Rcode -eq 0) ($r | Out-String)


$r = Send-Packet (New-Packet 6 $host1 0x20 "10.99.0.2")
Check "release by non-owner -> ACT_ERR" ($r -and $r.Opcode -eq 6 -and $r.Rcode -eq 6) ($r | Out-String)

$grp = "GRP" + (Get-Random -Maximum 99999)
$r1 = Send-Packet (New-Packet 5 $grp 0x1C "10.99.1.1" 0x8000)
$r2 = Send-Packet (New-Packet 5 $grp 0x1C "10.99.1.2" 0x8000)
Check "two members join internet group <1C>" ($r1 -and $r2 -and $r1.Rcode -eq 0 -and $r2.Rcode -eq 0) ""
$r = Send-Packet (New-Packet 0 $grp 0x1C "")
Check "query <1C> group -> both members" ($r -and $r.Addresses.Count -eq 2 -and ($r.Addresses -contains "10.99.1.1") -and ($r.Addresses -contains "10.99.1.2")) ($r | Out-String)

$r1 = Send-Packet (New-Packet 5 $grp 0x00 "10.99.1.1" 0x8000)
$r = Send-Packet (New-Packet 0 $grp 0x00 "")
Check "query normal group <00> -> 255.255.255.255" ($r -and $r.Addresses.Count -eq 1 -and $r.Addresses[0] -eq "255.255.255.255") ($r | Out-String)

$r = Send-Packet (New-Packet 0 $host1 0x20 "" 0 0 0x0110) 700
Check "broadcast query is ignored (no reply)" ($null -eq $r) ($r | Out-String)

$r = Send-Packet ([byte[]](1..40)) 700
Check "garbage datagram is ignored (no reply)" ($null -eq $r) ($r | Out-String)

$r = Send-Packet (New-Packet 6 $host1 0x20 "10.99.0.1")
Check "release by owner -> positive" ($r -and $r.Opcode -eq 6 -and $r.Rcode -eq 0) ($r | Out-String)

$r = Send-Packet (New-Packet 0 $host1 0x20 "")
Check "query after release -> negative" ($r -and $r.Rcode -eq 3) ($r | Out-String)

# ----- node status (nbtstat -A): the server answers for the machine it runs on -----
# Needs Wins:RegisterSelf (the default). The query is for the wildcard name "*", type NBSTAT (0x21).
$status = if ($Dashboard) { Invoke-RestMethod -Headers $Csrf "$Dashboard/api/status" -ErrorAction SilentlyContinue } else { $null }
if ($Dashboard -and $status -and $status.self.enabled -and $status.self.addresses.Count -gt 0) {
    $script:trn++
    $wild = [byte[]]@(0x20, 0x43, 0x4B) + [byte[]](@(0x41) * 30) + [byte[]]@(0)
    $pkt = (U16 $script:trn) + (U16 0) + (U16 1) + (U16 0) + (U16 0) + (U16 0) + $wild + (U16 0x21) + (U16 1)
    $udp = New-Object System.Net.Sockets.UdpClient
    $udp.Client.ReceiveTimeout = 3000
    [void]$udp.Send([byte[]]$pkt, $pkt.Length, $Server, $Port)
    $remote = New-Object System.Net.IPEndPoint ([System.Net.IPAddress]::Any, 0)
    $reply = $null
    try { $reply = $udp.Receive([ref]$remote) } catch { }
    $udp.Close()

    $ok = $reply -and $reply.Length -gt 57 -and (([int]$reply[46] -shl 8) -bor $reply[47]) -eq 0x21
    Check "node status query (nbtstat -A) is answered" $ok
    if ($ok) {
        $count = $reply[56]
        $names = 0..($count - 1) | ForEach-Object {
            $p = 57 + $_ * 18
            [pscustomobject]@{
                Name   = [System.Text.Encoding]::ASCII.GetString($reply, $p, 15).TrimEnd()
                Suffix = "{0:X2}" -f $reply[$p + 15]
                Group  = ($reply[$p + 16] -band 0x80) -ne 0
            }
        }
        $own = $status.self.name
        Check "it lists this machine's own name for every published suffix ($($status.self.suffixes -join ', '))" (-not ($status.self.suffixes | Where-Object { $s = $_; -not ($names | Where-Object { $_.Name -eq $own -and $_.Suffix -eq $s -and -not $_.Group }) })) ($names | Out-String)
        if ($status.self.group) {
            Check "it lists the workgroup '$($status.self.group)' as a group name" ([bool]($names | Where-Object { $_.Name -eq $status.self.group -and $_.Suffix -eq "00" -and $_.Group })) ($names | Out-String)
            $r = Send-Packet (New-Packet 0 $status.self.group 0x00 "")
            Check "the workgroup name is registered (resolves as a group)" ($r -and $r.Rcode -eq 0 -and $r.Addresses[0] -eq "255.255.255.255") ($r | Out-String)
        }
        Check "the reply ends with the 46-byte statistics block (MAC first)" ($reply.Length -eq 57 + $count * 18 + 46) "length=$($reply.Length)"
    }

    # A node status question about some OTHER machine's name is not this server's to answer.
    $script:trn++
    $pkt = (U16 $script:trn) + (U16 0) + (U16 1) + (U16 0) + (U16 0) + (U16 0) + (Encode-Name "SOMEONE-ELSE" 0x00) + (U16 0x21) + (U16 1)
    Check "node status for another machine's name gets no answer" ($null -eq (Send-Packet ([byte[]]$pkt) 700))
}

# ----- unique-name conflict: a second address claims a name that is already registered -----
$dup = "DUP" + (Get-Random -Maximum 99999)
[void](Send-Packet (New-Packet 5 $dup 0x20 "10.99.0.1"))
if ($Policy -eq "Reject") {
    $r = Send-Packet (New-Packet 5 $dup 0x20 "10.99.0.2")
    Check "conflict (Reject) -> ACT_ERR" ($r -and $r.Opcode -eq 5 -and $r.Rcode -eq 6) ($r | Out-String)
    $r = Send-Packet (New-Packet 0 $dup 0x20 "")
    Check "name stays with the first owner" ($r -and $r.Addresses[0] -eq "10.99.0.1") ($r | Out-String)
    [void](Send-Packet (New-Packet 6 $dup 0x20 "10.99.0.1"))
}
else {
    $pair = Send-Packet (New-Packet 5 $dup 0x20 "10.99.0.2") 4000 2
    $wack = $pair[0]; $final = $pair[1]
    Check "conflict (Challenge) -> WACK first (opcode 7, wait 3s)" ($wack -and $wack.Response -and $wack.Opcode -eq 7 -and $wack.AnCount -eq 1 -and $wack.Ttl -eq 3) ($wack | Out-String)
    Check "undefended name is granted to the newcomer (opcode 5, RCODE 0)" ($final -and $final.Opcode -eq 5 -and $final.Rcode -eq 0 -and $final.Trn -eq $wack.Trn -and $final.Addresses[0] -eq "10.99.0.2") ($final | Out-String)
    $r = Send-Packet (New-Packet 0 $dup 0x20 "")
    Check "name now resolves to the new owner" ($r -and $r.Addresses[0] -eq "10.99.0.2") ($r | Out-String)
    [void](Send-Packet (New-Packet 6 $dup 0x20 "10.99.0.2"))
}

foreach ($member in "10.99.1.1", "10.99.1.2") { [void](Send-Packet (New-Packet 6 $grp 0x1C $member 0x8000)) }
[void](Send-Packet (New-Packet 6 $grp 0x00 "10.99.1.1" 0x8000))

# ----- multihomed machine: the owner answers the challenge and lists the claimant's address too -----
# A stand-in "owner" on 127.0.0.2 answers the server's challenge. Windows does not deliver loopback
# traffic for port 137 to a test socket, so the server must be started with Wins__ChallengePort=<port>.
if ($Policy -eq "Challenge" -and $Server -eq "127.0.0.1" -and $ChallengePort -gt 0) {
    $owner = $null
    try { $owner = New-Object System.Net.Sockets.UdpClient (New-Object System.Net.IPEndPoint ([System.Net.IPAddress]::Parse("127.0.0.2"), $ChallengePort)) }
    catch { Write-Host "  SKIP  multihomed challenge (cannot bind 127.0.0.2:$ChallengePort)" -ForegroundColor Yellow }

    if ($owner) {
        try {
            $owner.Client.ReceiveTimeout = 3000
            foreach ($case in @(
                @{ Name = "same machine, second adapter -> both addresses kept"; Listed = @("127.0.0.2", "127.0.0.3"); Rcode = 0; Expect = @("127.0.0.2", "127.0.0.3") },
                @{ Name = "different machine, owner defends -> ACT_ERR";         Listed = @("127.0.0.2");              Rcode = 6; Expect = @("127.0.0.2") })) {

                $mh = "MH" + (Get-Random -Maximum 99999)
                [void](Send-Packet (New-Packet 5 $mh 0x20 "127.0.0.2"))

                $claim = New-Object System.Net.Sockets.UdpClient
                $claim.Client.ReceiveTimeout = 4000
                $pkt = New-Packet 8 $mh 0x20 "127.0.0.3"
                [void]$claim.Send($pkt, $pkt.Length, $Server, $Port)

                # the stand-in owner answers the server's challenge query
                $from = New-Object System.Net.IPEndPoint ([System.Net.IPAddress]::Any, 0)
                $q = $owner.Receive([ref]$from)
                $answer = [byte[]]@($q[0], $q[1], 0x85, 0x00, 0, 0, 0, 1, 0, 0, 0, 0) + $q[12..45] + (U16 0x20) + (U16 1) + (U32 300000) + (U16 (6 * $case.Listed.Count))
                foreach ($ip in $case.Listed) { $answer += (U16 0x6000) + [System.Net.IPAddress]::Parse($ip).GetAddressBytes() }
                [void]$owner.Send([byte[]]$answer, $answer.Length, $from)

                $wack = Read-Reply $claim; $final = Read-Reply $claim; $claim.Close()
                Check "multihomed: $($case.Name)" ($wack -and $wack.Opcode -eq 7 -and $final -and $final.Opcode -eq 8 -and $final.Rcode -eq $case.Rcode) ($final | Out-String)

                $r = Send-Packet (New-Packet 0 $mh 0x20 "")
                $same = $r -and $r.Addresses.Count -eq $case.Expect.Count -and -not ($case.Expect | Where-Object { $r.Addresses -notcontains $_ })
                Check "multihomed: name resolves to $($case.Expect -join ' + ')" $same ($r | Out-String)

                foreach ($ip in $case.Expect) { [void](Send-Packet (New-Packet 6 $mh 0x20 $ip)) }
            }
        }
        finally { $owner.Close() }
    }
}

# ----- partner server: a name unknown here is asked of a partner WINS and the answer relayed -----
# A stand-in partner listens on 127.0.0.2:13701 and is added through the dashboard API.
if ($Dashboard -and $Server -eq "127.0.0.1") {
    $partner = New-Object System.Net.Sockets.UdpClient (New-Object System.Net.IPEndPoint ([System.Net.IPAddress]::Parse("127.0.0.2"), 13701))
    try {
        $partner.Client.ReceiveTimeout = 3000
        $body = '{"name":"smoke partner","address":"127.0.0.2","port":13701,"enabled":true}'
        [void](Invoke-RestMethod -Method Post -Headers $Csrf -Uri "$Dashboard/api/partners" -ContentType "application/json" -Body $body)

        $remote = "REMOTE" + (Get-Random -Maximum 99999)
        $client = New-Object System.Net.Sockets.UdpClient
        $client.Client.ReceiveTimeout = 4000
        $pkt = New-Packet 0 $remote 0x20 ""
        [void]$client.Send($pkt, $pkt.Length, $Server, $Port)

        $from = New-Object System.Net.IPEndPoint ([System.Net.IPAddress]::Any, 0)
        $q = $partner.Receive([ref]$from)
        $answer = [byte[]]@($q[0], $q[1], 0x85, 0x80, 0, 0, 0, 1, 0, 0, 0, 0) + $q[12..45] + (U16 0x20) + (U16 1) + (U32 300000) + (U16 6) + (U16 0x6000) + [byte[]]@(10, 77, 0, 9)
        [void]$partner.Send([byte[]]$answer, $answer.Length, $from)

        $r = Read-Reply $client; $client.Close()
        Check "partner: unknown name is answered from the partner server" ($r -and $r.Rcode -eq 0 -and $r.Addresses[0] -eq "10.77.0.9") ($r | Out-String)

        $r = Send-Packet (New-Packet 0 $remote 0x20 "")
        Check "partner: repeat query is answered from cache (partner not asked again)" ($r -and $r.Addresses[0] -eq "10.77.0.9" -and $partner.Available -eq 0) ($r | Out-String)

        $row = (Invoke-RestMethod -Headers $Csrf "$Dashboard/api/names?source=partner").items | Where-Object name -eq $remote
        Check "partner: the forwarded name is listed with source Partner, via 'smoke partner'" ($row -and $row.source -eq "Partner" -and $row.origin -eq "smoke partner" -and $row.addresses[0] -eq "10.77.0.9") ($row | Out-String)

        $stats = (Invoke-RestMethod -Headers $Csrf "$Dashboard/api/partners") | Where-Object address -eq "127.0.0.2"
        Check "partner: counters show 1 asked / 1 answered" ($stats.queries -eq 1 -and $stats.answers -eq 1) ($stats | Out-String)

        # Cache poisoning guard: a partner answer that is not a real host address is neither relayed nor cached.
        $bogus = "BOGUS" + (Get-Random -Maximum 99999)
        $client = New-Object System.Net.Sockets.UdpClient
        $client.Client.ReceiveTimeout = 4000
        $pkt = New-Packet 0 $bogus 0x20 ""
        [void]$client.Send($pkt, $pkt.Length, $Server, $Port)
        $q = $partner.Receive([ref]$from)
        $answer = [byte[]]@($q[0], $q[1], 0x85, 0x80, 0, 0, 0, 1, 0, 0, 0, 0) + $q[12..45] + (U16 0x20) + (U16 1) + (U32 300000) + (U16 6) + (U16 0x6000) + [byte[]]@(224, 0, 0, 1)
        [void]$partner.Send([byte[]]$answer, $answer.Length, $from)
        $r = Read-Reply $client; $client.Close()
        Check "partner: an answer pointing at a multicast address is discarded (client gets 'not found')" ($r -and $r.Rcode -eq 3) ($r | Out-String)

        # A forged reply - right name, wrong transaction id - must be ignored; the real one that follows is used.
        $forged = "FORGED" + (Get-Random -Maximum 99999)
        $client = New-Object System.Net.Sockets.UdpClient
        $client.Client.ReceiveTimeout = 4000
        $pkt = New-Packet 0 $forged 0x20 ""
        [void]$client.Send($pkt, $pkt.Length, $Server, $Port)
        $q = $partner.Receive([ref]$from)
        $tail = $q[12..45] + (U16 0x20) + (U16 1) + (U32 300000) + (U16 6) + (U16 0x6000)
        $fake = [byte[]]@(($q[0] -bxor 0x55), $q[1], 0x85, 0x80, 0, 0, 0, 1, 0, 0, 0, 0) + $tail + [byte[]]@(10, 66, 6, 6)
        $real = [byte[]]@($q[0], $q[1], 0x85, 0x80, 0, 0, 0, 1, 0, 0, 0, 0) + $tail + [byte[]]@(10, 77, 0, 10)
        [void]$partner.Send([byte[]]$fake, $fake.Length, $from)
        [void]$partner.Send([byte[]]$real, $real.Length, $from)
        $r = Read-Reply $client; $client.Close()
        Check "partner: a reply with the wrong transaction id is ignored" ($r -and $r.Addresses[0] -eq "10.77.0.10") ($r | Out-String)

        $cleared = Invoke-RestMethod -Method Post -Headers $Csrf -Uri "$Dashboard/api/cache/clear"
        $left = (Invoke-RestMethod -Headers $Csrf "$Dashboard/api/names?source=partner").items
        Check "cache clear drops every forwarded answer ($($cleared.cleared) entries)" ($cleared.cleared -ge 2 -and @($left).Count -eq 0)

        # Microsoft WINS says "not found" with every count at zero and the name trailing the header.
        $missing = "NOSUCH" + (Get-Random -Maximum 99999)
        $client = New-Object System.Net.Sockets.UdpClient
        $client.Client.ReceiveTimeout = 4000
        $pkt = New-Packet 0 $missing 0x20 ""
        [void]$client.Send($pkt, $pkt.Length, $Server, $Port)
        $q = $partner.Receive([ref]$from)
        $answer = [byte[]]@($q[0], $q[1], 0x85, 0x83, 0, 0, 0, 0, 0, 0, 0, 0) + $q[12..45] + (U16 0x0A) + (U16 1) + (U32 0) + (U16 0)
        [void]$partner.Send([byte[]]$answer, $answer.Length, $from)
        $r = Read-Reply $client; $client.Close()
        Check "partner: Microsoft-style 'not found' reaches the client as a negative answer" ($r -and $r.Rcode -eq 3) ($r | Out-String)
        $stats = (Invoke-RestMethod -Headers $Csrf "$Dashboard/api/partners") | Where-Object address -eq "127.0.0.2"
        # 2 = this reply + the discarded multicast answer above (an unusable answer counts as "not found")
        Check "partner: counted as 'not found', not as 'no reply'" ($stats.notFound -eq 2 -and $stats.noReply -eq 0) ($stats | Out-String)
    }
    finally {
        $partner.Close()
        try { [void](Invoke-RestMethod -Method Delete -Headers $Csrf -Uri "$Dashboard/api/partners/127.0.0.2") } catch { }
    }
}

if ($script:failed -gt 0) { Write-Host "`n$($script:failed) check(s) failed." -ForegroundColor Red; exit 1 }
Write-Host "`nAll checks passed." -ForegroundColor Green
