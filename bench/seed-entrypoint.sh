#!/bin/bash
set -euo pipefail
if [ "${PARITY_REDIS:-}" = "1" ]; then
    redis-server --daemonize yes --save "" --appendonly no --logfile /dev/null --pidfile /tmp/campfire-seed-redis.pid
fi
exec "$@"
