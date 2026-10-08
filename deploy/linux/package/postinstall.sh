#!/bin/sh
# Runs after the package is unpacked, on install and on every upgrade (deb: "configure"; rpm: $1 = 1 or 2).
# install.sh does the work: same checks, files and service as a manual install; an upgrade keeps
# appsettings.json and everything in /var/lib/winsalt, and restarts the service.
set -e
case "${1:-}" in
  configure|1|2) /usr/lib/winsalt/install.sh --packaged ;;
esac
exit 0
