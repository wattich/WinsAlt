#!/bin/bash
# =============================================================================
# install.sh - installs (or upgrades) WinsAlt as a systemd service.
#
#   sudo ./install.sh                 install / upgrade, then enable and start the service
#   sudo ./install.sh --no-start      install / upgrade only
#   sudo ./install.sh --no-firewall   do not open the ports in an active ufw / firewalld
#   sudo ./install.sh --force         install on a distribution that is not on the supported list
#   install.sh --packaged             used by the .deb / .rpm scripts (see below)
#
#   /opt/winsalt/winsalt             the program
#   /opt/winsalt/appsettings.json    base configuration (kept on upgrade)
#   /var/lib/winsalt/                data: names, static mappings, password, settings saved from the dashboard
#   /etc/systemd/system/winsalt.service
#
# Before installing it checks the distribution, installs what the program needs (OpenSSL, iproute2, curl)
# and checks that its ports are free. A fresh install opens the dashboard to the LAN; it asks for a
# sign-in before showing anything, and the first sign-in is admin / admin (the page then insists on a new one).
#
# The .deb and .rpm packages carry this same script in /usr/lib/winsalt and run it with --packaged after
# unpacking. In that mode it never calls apt-get / dnf (the package manager is busy installing this package;
# the packages declare OpenSSL, iproute and curl as dependencies instead), and an unlisted distribution is a
# warning, not a stop. Everything else (ports, files, service, firewall) is the same as a manual install.
# =============================================================================
set -euo pipefail
# Never "cmd | grep -q": with pipefail, grep -q exits at the first match and cmd dies of SIGPIPE, so the test
# fails even though it matched (seen on Ubuntu 26.04 with ldconfig). Let grep read everything: > /dev/null.

START=1 FIREWALL=1 FORCE=0 PACKAGED=0
for arg in "$@"; do
  case "$arg" in
    --no-start) START=0 ;;
    --no-firewall) FIREWALL=0 ;;
    --force) FORCE=1 ;;
    --packaged) PACKAGED=1 ;;
    *) echo "Unknown option: $arg  (use --no-start, --no-firewall, --force)" >&2; exit 2 ;;
  esac
done

fail() { echo "ERROR: $*" >&2; exit 1; }
[ "$(id -u)" -eq 0 ] || fail "Run as root (sudo ./install.sh)"
command -v systemctl > /dev/null || fail "systemd is required"
HERE="$(cd "$(dirname "$0")" && pwd)"

# ---------- 1. distribution ----------
# The versions .NET 10 supports that this project builds and tests for (docs/ROADMAP.md).
. /etc/os-release 2> /dev/null || fail "/etc/os-release not found - cannot tell which Linux this is"
case "${ID:-}:${VERSION_ID:-}" in
  debian:13|ubuntu:24.04|ubuntu:26.04|centos:10) echo "Distribution: ${PRETTY_NAME:-$ID $VERSION_ID} (supported)" ;;
  *)
    echo "Distribution: ${PRETTY_NAME:-${ID:-unknown} ${VERSION_ID:-}} is not on the supported list" >&2
    echo "  Supported: Debian 13, Ubuntu 24.04 / 26.04, CentOS Stream 10" >&2
    [ "$FORCE" -eq 1 ] || [ "$PACKAGED" -eq 1 ] || fail "Stopped. Run again with --force to install anyway (untested)."
    echo "  Installing anyway (untested)" >&2 ;;
esac
[ "$(uname -m)" = "x86_64" ] || fail "This package is for x86_64; this machine is $(uname -m)"

# ---------- 2. packages the program and this script need ----------
if command -v apt-get > /dev/null; then PM=apt
elif command -v dnf > /dev/null; then PM=dnf
else PM=none; fi

# A package's dependencies may have been unpacked a moment ago: refresh the library cache first.
[ "$PACKAGED" -eq 1 ] && ldconfig 2> /dev/null || true
have_libssl() { ldconfig -p 2> /dev/null | grep 'libssl\.so\.3 ' > /dev/null; }
missing=()
have_libssl || missing+=(libssl)
command -v ss > /dev/null || missing+=(ss)
command -v curl > /dev/null || missing+=(curl)

if [ ${#missing[@]} -gt 0 ] && [ "$PACKAGED" -eq 1 ]; then
  fail "Missing: ${missing[*]} - the package dependencies should have installed them; install them by hand and run: $HERE/install.sh --packaged"
fi
if [ ${#missing[@]} -gt 0 ]; then
  echo "Installing what is missing: ${missing[*]}"
  case "$PM" in
    apt)
      pkgs=()
      for m in "${missing[@]}"; do
        case "$m" in
          # Debian 13 / Ubuntu 24.04+ name it libssl3t64; older releases libssl3.
          libssl) if apt-cache show libssl3t64 > /dev/null 2>&1; then pkgs+=(libssl3t64); else pkgs+=(libssl3); fi ;;
          ss) pkgs+=(iproute2) ;;
          curl) pkgs+=(curl) ;;
        esac
      done
      apt-get update -qq
      DEBIAN_FRONTEND=noninteractive apt-get install -y -qq "${pkgs[@]}" ;;
    dnf)
      pkgs=()
      for m in "${missing[@]}"; do
        case "$m" in libssl) pkgs+=(openssl-libs) ;; ss) pkgs+=(iproute) ;; curl) pkgs+=(curl) ;; esac
      done
      dnf install -y -q "${pkgs[@]}" ;;
    *) fail "No apt-get or dnf to install: ${missing[*]} - install them by hand and run again" ;;
  esac
fi
have_libssl || fail "OpenSSL 3 (libssl.so.3) is still missing - WinsAlt needs it for password hashing"

# The program itself: every library it links must resolve (catches a C library older than the build's).
if ldd "$HERE/winsalt" 2>&1 | grep 'not found' > /dev/null; then
  ldd "$HERE/winsalt" 2>&1 | grep 'not found' >&2
  fail "This system cannot run the WinsAlt binary (missing library or C library too old)"
fi

# ---------- 3. ports ----------
UPGRADE=0
if systemctl is-active --quiet winsalt 2> /dev/null; then
  UPGRADE=1
  echo "Stopping the running service"
  systemctl stop winsalt
fi

if systemctl is-active --quiet nmbd 2> /dev/null; then
  fail "Samba's nmbd is running and holds UDP 137. Stop it first:  systemctl disable --now nmbd   (smbd can stay)"
fi

CONFIG=/opt/winsalt/appsettings.json
[ -f "$CONFIG" ] || CONFIG="$HERE/appsettings.json"
dash_url() { grep -o '"Url": *"[^"]*"' "$CONFIG" | head -1 | sed 's/.*"\([^"]*\)"$/\1/'; }
DASH_PORT="$(dash_url | grep -o '[0-9]*$' || true)"; DASH_PORT="${DASH_PORT:-8137}"
REPL_PORT="$(grep -o '"ReplicationPort": *[0-9]*' "$CONFIG" | grep -o '[0-9]*$' || true)"; REPL_PORT="${REPL_PORT:-8138}"

# Prints the process holding a port ("" = free). $1 = u|t, $2 = port.
holder() {
  local out
  out="$(ss -H -ln"$1"p "( sport = :$2 )" 2> /dev/null || true)"
  [ -n "$out" ] || return 0
  echo "$out" | grep -o 'users:(("[^"]*"' | head -1 | sed 's/users:(("//; s/"//g' | grep . || echo "another process"
}

who="$(holder u 137)"
[ -z "$who" ] || fail "UDP 137 (the name service) is in use by: $who - stop it first"
who="$(holder t "$REPL_PORT")"
[ -z "$who" ] || fail "TCP $REPL_PORT (replication) is in use by: $who - stop it, or change Wins:ReplicationPort in $CONFIG"

who="$(holder t "$DASH_PORT")"
if [ -n "$who" ]; then
  # Same fall-backs as the Windows installer; the chosen port is written into appsettings.json below.
  for p in 8139 18137 9137; do
    if [ -z "$(holder t "$p")" ] && [ "$p" != "$REPL_PORT" ]; then
      echo "Dashboard port $DASH_PORT is in use by: $who - using $p instead"
      NEW_DASH_PORT=$p; break
    fi
  done
  [ -n "${NEW_DASH_PORT:-}" ] || fail "Dashboard port $DASH_PORT is in use by: $who, and no fall-back port is free"
fi

# ---------- 4. files and service ----------
install -d -m 755 /opt/winsalt
install -d -m 750 /var/lib/winsalt
install -m 755 "$HERE/winsalt" /opt/winsalt/winsalt
[ -f /opt/winsalt/appsettings.json ] || install -m 644 "$HERE/appsettings.json" /opt/winsalt/appsettings.json
CONFIG=/opt/winsalt/appsettings.json
if [ -n "${NEW_DASH_PORT:-}" ]; then
  sed -i "s#\(\"Url\": *\"[^\"]*:\)$DASH_PORT\"#\1$NEW_DASH_PORT\"#" "$CONFIG"
  DASH_PORT=$NEW_DASH_PORT
fi
[ -f /var/lib/winsalt/static-mappings.json ] || install -m 640 "$HERE/static-mappings.json" /var/lib/winsalt/static-mappings.json
install -m 644 "$HERE/winsalt.service" /etc/systemd/system/winsalt.service
systemctl daemon-reload

# Is the dashboard open to other computers? appsettings.json's host, unless the Settings page decided.
LAN=1
dash_url | grep -E '//(127\.|localhost|\[::1\])' > /dev/null && LAN=0
if [ -f /var/lib/winsalt/settings.json ]; then
  tr -d ' \n' < /var/lib/winsalt/settings.json | grep '"Dashboard:RemoteAccess":"true"' > /dev/null && LAN=1
  tr -d ' \n' < /var/lib/winsalt/settings.json | grep '"Dashboard:RemoteAccess":"false"' > /dev/null && LAN=0
fi

# ---------- 5. firewall ----------
# Only a firewall that is already running is touched; none is installed or switched on. Rule names /
# comments match the dashboard's Firewall table, so it shows them as allowed.
if [ "$FIREWALL" -eq 1 ]; then
  ports=("137/udp|WinsAlt NBNS (UDP 137)" "$REPL_PORT/tcp|WinsAlt replication (TCP $REPL_PORT)")
  [ "$LAN" -eq 1 ] && ports+=("$DASH_PORT/tcp|WinsAlt dashboard")
  if command -v firewall-cmd > /dev/null && firewall-cmd --state > /dev/null 2>&1; then
    for entry in "${ports[@]}"; do
      firewall-cmd -q --add-port="${entry%%|*}" 2> /dev/null || true
      firewall-cmd -q --permanent --add-port="${entry%%|*}" 2> /dev/null || true
    done
    echo "firewalld: opened ${ports[*]%%|*} in the default zone"
  elif command -v ufw > /dev/null && LC_ALL=C ufw status 2> /dev/null | grep '^Status: active' > /dev/null; then
    for entry in "${ports[@]}"; do ufw allow "${entry%%|*}" comment "${entry#*|}" > /dev/null; done
    echo "ufw: allowed $(printf '%s ' "${ports[@]%%|*}")"
  fi
fi

if [ "$START" -eq 1 ] || [ "$UPGRADE" -eq 1 ]; then
  systemctl enable --now winsalt
  sleep 2
  systemctl --no-pager --lines=0 status winsalt || true
  # Asks the running service (its public /api/info) whether the UDP 137 listener came up: 0 yes, 1 no, 2 no answer.
  set +e; /opt/winsalt/winsalt --listener-state "$DASH_PORT"; LISTENER=$?; set -e
  case "$LISTENER" in
    0) echo "Name server: listening on UDP 137." ;;
    1) echo "WARNING: the name server is NOT listening on UDP 137 - see: journalctl -u winsalt" >&2 ;;
    *) echo "WARNING: the service did not answer yet - check: systemctl status winsalt" >&2 ;;
  esac
fi

# ---------- 6. where to go next ----------
IP="$(hostname -I 2> /dev/null | awk '{print $1}')"
echo
case "$(hostname -s 2> /dev/null | tr '[:upper:]' '[:lower:]')" in
  localhost|"")
    echo "WARNING: this machine is still called \"$(hostname)\" - WinsAlt does not publish that name." >&2
    echo "  Set a real one (hostnamectl set-hostname <name>) or Wins:SelfName, then: systemctl restart winsalt" >&2 ;;
esac
if [ "$LAN" -eq 1 ]; then
  echo "Installed. Dashboard: http://${IP:-<this server>}:$DASH_PORT   (from other computers too)"
else
  echo "Installed. Dashboard: http://127.0.0.1:$DASH_PORT   (this machine only - switch on"
  echo "  \"Open the dashboard to other computers\" in Settings, or use: ssh -L $DASH_PORT:127.0.0.1:$DASH_PORT ${SUDO_USER:-$(id -un)}@${IP:-<this server>})"
fi
if [ ! -f /var/lib/winsalt/auth.json ]; then
  echo "Sign in with admin / admin, then choose your own password straight away."
fi
echo "Logs: journalctl -u winsalt -f"
DOCS="$HERE"; [ -d /usr/share/doc/winsalt ] && [ "$PACKAGED" -eq 1 ] && DOCS=/usr/share/doc/winsalt
echo "License: MIT (LICENSE). Scope and limitations of use: usage-terms.en.txt / usage-terms.th.txt in $DOCS"
