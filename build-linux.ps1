# =============================================================================
# build-linux.ps1 - runs build-linux.sh inside WSL (Debian) from Windows.
#
#   powershell -ExecutionPolicy Bypass -File build-linux.ps1   -> publish\linux-x64\
#
# One-time setup inside WSL (as root): apt-get install clang zlib1g-dev rsync, and the .NET 10 SDK in
# /opt/dotnet10 (dotnet-install.sh --channel 10.0 --install-dir /opt/dotnet10 --no-path).
# =============================================================================
$ErrorActionPreference = "Stop"
$distro = if ($env:WINSALT_WSL_DISTRO) { $env:WINSALT_WSL_DISTRO } else { "Debian" }

# F:\a b\c -> /mnt/f/a b/c ; passed as one argument, so spaces and parentheses in the path are fine.
$full = (Resolve-Path $PSScriptRoot).Path
$linuxPath = "/mnt/" + $full.Substring(0, 1).ToLowerInvariant() + $full.Substring(2).Replace('\', '/')

# The script is checked out with Windows line endings; bash needs LF.
$script = Join-Path $PSScriptRoot "build-linux.sh"
$text = [IO.File]::ReadAllText($script)
if ($text.Contains("`r`n")) { [IO.File]::WriteAllText($script, $text.Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding $false)) }

wsl.exe -d $distro -u root -- bash "$linuxPath/build-linux.sh"
if ($LASTEXITCODE -ne 0) { throw "Linux build failed (exit $LASTEXITCODE)" }
