#!/bin/sh
# deb only: "apt purge winsalt" also removes the data (names, mappings, settings, password).
# rpm has no purge: after "dnf remove winsalt" the data stays in /var/lib/winsalt until deleted by hand.
set -e
if [ "${1:-}" = "purge" ]; then
  rm -rf /var/lib/winsalt
  echo "WinsAlt data removed (/var/lib/winsalt)."
fi
exit 0
