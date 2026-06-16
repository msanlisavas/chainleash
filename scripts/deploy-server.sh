#!/usr/bin/env bash
# CHAINLEASH server-side deploy. The CI deploy SSH key is pinned to ONLY run this script
# (forced command in ~/.ssh/authorized_keys, `restrict` — no pty/forwarding/shell). Even
# if that key leaked, it could trigger a redeploy and nothing else. App secrets live in
# ./.env and ./secrets on the server; this script never sees GitHub secrets.
set -euo pipefail

cd /home/murat/chainleash

# Fail closed: never bring the stack up with an empty/missing env (which would silently
# substitute blank secrets).
[ -r .env ] || { echo "FATAL: /home/murat/chainleash/.env missing or unreadable" >&2; exit 1; }

echo "==> git pull"
git pull --ff-only origin main

# Pin the deployed image to the commit we just pulled — CI pushed <sha> tags, so this
# runs the exact reviewed build rather than a mutable :latest.
IMAGE_TAG="$(git rev-parse HEAD)"
export IMAGE_TAG
echo "==> deploying image tag ${IMAGE_TAG}"

echo "==> pull images"
docker compose -f docker-compose.prod.yml pull

echo "==> up -d"
docker compose -f docker-compose.prod.yml up -d --remove-orphans

docker image prune -f >/dev/null 2>&1 || true
echo "==> deployed ${IMAGE_TAG:0:12} at $(date -u +%FT%TZ)"
