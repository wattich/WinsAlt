#!/bin/sh
# Runs before the package files are removed. Only a real removal takes the service away
# (deb: "remove"; rpm: $1 = 0); an upgrade (deb: "upgrade"; rpm: $1 = 1) leaves it to the new postinstall.
# The data in /var/lib/winsalt is kept; "apt purge winsalt" removes it (postremove).
set -e
case "${1:-}" in
  remove|0) /usr/lib/winsalt/uninstall.sh --packaged || true ;;
esac
exit 0
