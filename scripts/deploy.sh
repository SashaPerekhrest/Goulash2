#!/usr/bin/env sh
set -eu

: "${GHCR_OWNER:?GHCR_OWNER is required}"
: "${IMAGE_TAG:?IMAGE_TAG is required}"

COMPOSE_FILE="${COMPOSE_FILE:-compose.prod.yaml}"
export GHCR_OWNER IMAGE_TAG

docker compose -f "$COMPOSE_FILE" config --quiet
docker compose -f "$COMPOSE_FILE" pull
docker compose -f "$COMPOSE_FILE" up -d --remove-orphans --wait --wait-timeout 180
docker compose -f "$COMPOSE_FILE" exec -T frontend wget -q -O - http://127.0.0.1/api/v1/health/ready
docker compose -f "$COMPOSE_FILE" ps
