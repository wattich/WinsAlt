# =============================================================================
# build-aot.ps1 - Publish WinsAlt as a Native AOT single .exe.
#
# Two environment fixes are REQUIRED on this build machine, both applied below:
#   1. DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
#      The Thai (th-TH) system culture makes MSBuild's culture-sensitive
#      LastIndexOf("-") treat the hyphen as ignorable, returning the wrong index
#      and breaking the ILCompiler RID parsing ("win-x64".SubString(8) error).
#      Forcing invariant culture for the build fixes it. (Runtime is already
#      invariant via <InvariantGlobalization>.)
#   2. VS Installer dir on PATH
#      The Native AOT linker step calls vswhere.exe to locate the MSVC linker;
#      vswhere lives in the VS Installer folder which isn't on PATH by default.
#
# Prerequisite: Visual Studio Build Tools with "Desktop development with C++"
# (provides link.exe + the MSVC toolchain).
# =============================================================================

$ErrorActionPreference = "Stop"
$proj = Join-Path $PSScriptRoot "WinsAlt.csproj"
$out  = Join-Path $PSScriptRoot "publish\win-x64"

$env:DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = "1"
$installer = "C:\Program Files (x86)\Microsoft Visual Studio\Installer"
if (Test-Path $installer) { $env:PATH = "$installer;" + $env:PATH }

Write-Host "Publishing Native AOT -> $out" -ForegroundColor Cyan
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish $proj -c Release -r win-x64 -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

Write-Host "`nDone. Output:" -ForegroundColor Green
Get-ChildItem $out | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,2)}} | Format-Table -AutoSize
