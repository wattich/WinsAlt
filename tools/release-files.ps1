# =============================================================================
# release-files.ps1 - gathers the files of one version for a GitHub release.
#
#   powershell -ExecutionPolicy Bypass -File tools\release-files.ps1      # version from WinsAlt.csproj
#   -> Output\release-<version>\  WinsAlt_Setup_v<version>.exe
#                                 winsalt_<version>-1_amd64.deb
#                                 winsalt-<version>-1.x86_64.rpm
#                                 WinsAlt_v<version>_linux-x64.tar.gz
#                                 SHA256SUMS.txt   (sha256sum format: "sha256sum -c SHA256SUMS.txt" checks it)
#
# Build first: build-installer.ps1 (Windows) and build-linux.ps1 (Linux). Stops if a file is missing
# or older than the program it should contain.
# =============================================================================
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content (Join-Path $root "WinsAlt.csproj"))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$out = Join-Path $root "Output"

$names = @(
    "WinsAlt_Setup_v$version.exe",
    "winsalt_$version-1_amd64.deb",
    "winsalt-$version-1.x86_64.rpm",
    "WinsAlt_v${version}_linux-x64.tar.gz"
)
$missing = $names | Where-Object { -not (Test-Path (Join-Path $out $_)) }
if ($missing) { throw "Not built yet: $($missing -join ', ') (run build-installer.ps1 and build-linux.ps1)" }

# A package older than the binary it was made from means a build step was skipped.
$checks = @(
    @{ File = "WinsAlt_Setup_v$version.exe"; Binary = "publish\win-x64\WinsAlt.exe" },
    @{ File = "winsalt_$version-1_amd64.deb"; Binary = "publish\linux-x64\winsalt" }
)
foreach ($c in $checks) {
    $bin = Join-Path $root $c.Binary
    if ((Test-Path $bin) -and (Get-Item (Join-Path $out $c.File)).LastWriteTime -lt (Get-Item $bin).LastWriteTime) {
        throw "$($c.File) is older than $($c.Binary) - rebuild it"
    }
}

$dest = Join-Path $out "release-$version"
if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
[void](New-Item -ItemType Directory $dest)

$sums = foreach ($n in $names) {
    Copy-Item (Join-Path $out $n) $dest
    "{0}  {1}" -f (Get-FileHash -Algorithm SHA256 (Join-Path $dest $n)).Hash.ToLowerInvariant(), $n
}
# LF line ends and no BOM, so sha256sum on Linux reads it as is.
[IO.File]::WriteAllText((Join-Path $dest "SHA256SUMS.txt"), (($sums -join "`n") + "`n"), (New-Object Text.UTF8Encoding $false))

Write-Host "Release files for $version in $dest" -ForegroundColor Green
Get-ChildItem $dest | Format-Table Name, Length -AutoSize | Out-String | Write-Host
Get-Content (Join-Path $dest "SHA256SUMS.txt")
