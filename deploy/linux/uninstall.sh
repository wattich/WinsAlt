#!/bin/bash
# =============================================================================
# uninstall.sh - removes the WinsAlt service and program.
#
#   sudo ./uninstall.sh           keeps /var/lib/winsalt (names, mappings, settings, password)
#   sudo ./uninstall.sh --purge   removes it as well
#   uninstall.sh --packaged       used by the .deb / .rpm removal script (only the closing hint differs)
#
# ufw rules that carry a "WinsAlt ..." comment (added by install.sh or the dashboard) are deleted.
# firewalld ports have no owner to tell them apart, so they are listed for you to close by hand.
# =============================================================================
set -euo pipefail
[ "$(id -u)" -eq 0 ] || { echo "Run as root (sudo ./uninstall.sh)" >&2; exit 1; }

systemctl disable --now winsalt 2> /dev/null || true
rm -f /etc/systemd/system/winsalt.service
systemctl daemon-reload

if command -v ufw > /dev/null; then
  LC_ALL=C ufw status 2> /dev/null | grep '# WinsAlt' | grep -v '(v6)' | awk '{print $1}' | sort -u | while read -r spec; do
    ufw delete allow "$spec" > /dev/null && echo "ufw: removed $spec"
  done || true
fi
if command -v firewall-cmd > /dev/null && firewall-cmd --state > /dev/null 2>&1; then
  echo "firewalld: ports open in the default zone: $(firewall-cmd --list-ports)"
  echo "  close WinsAlt's with:  firewall-cmd --permanent --remove-port=<port>/<proto> && firewall-cmd --reload"
fi

rm -rf /opt/winsalt

if [ "${1:-}" = "--purge" ]; then
  rm -rf /var/lib/winsalt
  echo "WinsAlt removed, data included."
elif [ "${1:-}" = "--packaged" ]; then
  # apt purge removes the data itself (the package's postremove); rpm has no purge.
  if command -v dpkg > /dev/null && dpkg -s winsalt > /dev/null 2>&1; then
    echo "WinsAlt removed. Data kept in /var/lib/winsalt (remove it too with: apt purge winsalt)."
  else
    echo "WinsAlt removed. Data kept in /var/lib/winsalt (remove it too with: rm -rf /var/lib/winsalt)."
  fi
else
  echo "WinsAlt removed. Data kept in /var/lib/winsalt (remove with --purge)."
fi
