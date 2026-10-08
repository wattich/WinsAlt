# =============================================================================
# build-installer.ps1 - Native AOT publish, then compile the Inno Setup installer.
#
#   powershell -ExecutionPolicy Bypass -File build-installer.ps1
#   -> Output\WinsAlt_Setup_v<version>.exe
#
# Always republishes first so the installer can never ship a stale publish\ folder.
# Prerequisites: everything build-aot.ps1 needs, plus Inno Setup 6.
# =============================================================================

$ErrorActionPreference = "Stop"

$iscc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) { throw "Inno Setup 6 not found at $iscc" }

# The .iss carries its own version define - refuse to build if it drifted from the csproj.
$csprojVersion = ([xml](Get-Content (Join-Path $PSScriptRoot "WinsAlt.csproj"))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$issVersion = (Select-String -Path (Join-Path $PSScriptRoot "WinsAlt_Setup.iss") -Pattern '^#define MyAppVersion "(.+)"').Matches[0].Groups[1].Value
if ($csprojVersion -ne $issVersion) {
    throw "Version mismatch: WinsAlt.csproj is $csprojVersion but WinsAlt_Setup.iss is $issVersion"
}

& (Join-Path $PSScriptRoot "build-aot.ps1")

Write-Host "`nCompiling installer..." -ForegroundColor Cyan
& $iscc /Q (Join-Path $PSScriptRoot "WinsAlt_Setup.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit $LASTEXITCODE)" }

Write-Host "`nInstaller:" -ForegroundColor Green
Get-ChildItem (Join-Path $PSScriptRoot "Output\WinsAlt_Setup_v$issVersion.exe") |
    Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,2)}} | Format-Table -AutoSize
