#!/bin/bash
# =============================================================================
# build-linux.sh - Native AOT build of WinsAlt for Linux x64.
#
# Native AOT cannot cross-compile from Windows, so this runs ON Linux - on the
# dev machine that is WSL Debian 13 (driven from Windows by build-linux.ps1).
#
# Requirements on the build box: clang, zlib1g-dev, rsync, and a .NET 10 SDK.
# The SDK is looked for at /opt/dotnet10/dotnet (override with DOTNET=...):
# the WSL box also hosts another project, whose .NET 8 is the system
# `dotnet`; the two are kept apart and nothing here touches PATH.
#
# The sources are copied into the Linux file system first (WINSALT_BUILD_DIR,
# default /root/winsalt) - building straight off /mnt/f is very slow.
#
# Output: publish/linux-x64/ - winsalt (the binary), appsettings.json,
# static-mappings.json, winsalt.service, install.sh, uninstall.sh
# and in Output/: WinsAlt_v<ver>_linux-x64.tar.gz, winsalt_<ver>-1_amd64.deb,
# winsalt-<ver>-1.x86_64.rpm. The packages are made with nfpm (one static binary,
# kept in $WORK/tools and fetched with a checksum check the first time - nothing is
# installed on the build box); deploy/linux/package/ describes them.
# =============================================================================
set -euo pipefail

SRC="$(cd "$(dirname "$0")" && pwd)"
WORK="${WINSALT_BUILD_DIR:-/root/winsalt}"
DOTNET="${DOTNET:-/opt/dotnet10/dotnet}"
OUT="$SRC/publish/linux-x64"

[ -x "$DOTNET" ] || { echo "No .NET 10 SDK at $DOTNET (set DOTNET=...)" >&2; exit 1; }
command -v clang > /dev/null || { echo "clang is required for Native AOT: apt-get install clang zlib1g-dev" >&2; exit 1; }

echo "Copying sources to $WORK/src"
mkdir -p "$WORK/src"
rsync -a --delete \
  --exclude '/bin/' --exclude '/obj/' --exclude '/publish/' --exclude '/Output/' --exclude '/.vs/' --exclude '/.claude/' --exclude '/.git/' \
  "$SRC/" "$WORK/src/"

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
echo "Publishing Native AOT (linux-x64)"
rm -rf "$WORK/publish"
"$DOTNET" publish "$WORK/src/WinsAlt.csproj" -c Release -r linux-x64 -o "$WORK/publish" --nologo

rm -rf "$OUT"
mkdir -p "$OUT"
install -m 755 "$WORK/publish/WinsAlt" "$OUT/winsalt"
install -m 644 "$WORK/src/appsettings.json" "$WORK/src/static-mappings.json" "$WORK/src/deploy/linux/winsalt.service" "$OUT/"
install -m 755 "$WORK/src/deploy/linux/install.sh" "$WORK/src/deploy/linux/uninstall.sh" "$OUT/"
install -m 644 "$WORK/src/LICENSE" "$WORK/src/THIRD-PARTY-NOTICES.md" "$WORK/src/installer/usage-terms.en.txt" "$WORK/src/installer/usage-terms.th.txt" "$OUT/"
install -m 644 "$WORK/src/INSTALL.md" "$WORK/src/INSTALL.th.md" "$OUT/"
# Files edited on Windows may carry CRLF, which bash and systemd reject.
sed -i 's/\r$//' "$OUT"/*.sh "$OUT/winsalt.service"

# The file to hand out: unpack anywhere, then  sudo ./install.sh
VERSION=$(grep -o '<Version>[^<]*' "$SRC/WinsAlt.csproj" | cut -d'>' -f2)
TARBALL="$SRC/Output/WinsAlt_v${VERSION}_linux-x64.tar.gz"
mkdir -p "$SRC/Output"
tar -czf "$TARBALL" -C "$SRC/publish" --owner=0 --group=0 --mode="u+rwX,go+rX,go-w" --transform "s|^linux-x64|winsalt-$VERSION|" linux-x64

# ---------- .deb and .rpm ----------
NFPM_VERSION=2.47.0
NFPM_SHA256=0660ca602b2d2d2ae4781a06c692b3eeb9d437ffea05b831d76e41f4a3188783
NFPM="$WORK/tools/nfpm"
if [ ! -x "$NFPM" ]; then
  echo "Fetching nfpm $NFPM_VERSION into $WORK/tools"
  mkdir -p "$WORK/tools"
  curl -fsSL -o "$WORK/tools/nfpm.tgz" "https://github.com/goreleaser/nfpm/releases/download/v$NFPM_VERSION/nfpm_${NFPM_VERSION}_Linux_x86_64.tar.gz"
  echo "$NFPM_SHA256  $WORK/tools/nfpm.tgz" | sha256sum -c --quiet
  tar -xzf "$WORK/tools/nfpm.tgz" -C "$WORK/tools" nfpm
fi
STAGE="$WORK/stage"
rm -rf "$STAGE"
cp -a "$OUT" "$STAGE"
cp -a "$WORK/src/deploy/linux/package" "$STAGE/package"
sed -i 's/\r$//' "$STAGE"/package/*.sh
sed -e "s|\${VERSION}|$VERSION|g" -e "s|\${STAGE}|$STAGE|g" "$STAGE/package/nfpm.yaml" > "$STAGE/nfpm.yaml"
rm -f "$SRC/Output/winsalt_${VERSION}-1_amd64.deb" "$SRC/Output/winsalt-${VERSION}-1.x86_64.rpm"
"$NFPM" package -f "$STAGE/nfpm.yaml" -p deb -t "$SRC/Output/"
"$NFPM" package -f "$STAGE/nfpm.yaml" -p rpm -t "$SRC/Output/"

echo
echo "Done: $OUT"
ls -l "$OUT"
echo "Packages:"
ls -l "$TARBALL" "$SRC/Output/winsalt_${VERSION}-1_amd64.deb" "$SRC/Output/winsalt-${VERSION}-1.x86_64.rpm"
