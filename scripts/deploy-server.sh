#!/usr/bin/env bash
# CHAINLEASH server-side deploy. The CI deploy SSH key is pinned to ONLY run this script
# (forced command in ~/.ssh/authorized_keys, no-pty/no-forwarding) — even if that key
# leaked, it could trigger a redeploy and nothing else. App secrets live in ./.env and
# ./secrets on the server; this script never sees GitHub secrets.
set -euo pipefail

cd /home/murat/chainleash

echo "==> git pull"
git pull --ff-only origin main

echo "==> pull images"
docker compose -f docker-compose.prod.yml pull

echo "==> up -d"
docker compose -f docker-compose.prod.yml up -d --remove-orphans

docker image prune -f >/dev/null 2>&1 || true
echo "==> deployed $(git rev-parse --short HEAD) at $(date -u +%FT%TZ)"
