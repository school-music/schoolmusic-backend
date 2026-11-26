#!/usr/bin/env bash
set -euo pipefail

PROJECT_NAME="${COMPOSE_VERIFY_PROJECT:-schoolmusic_verify_$$}"
API_PORT="${COMPOSE_VERIFY_PORT:-18080}"
DOCKER_BIN="${DOCKER_BIN:-docker}"
ENV_FILE=".verify.compose.$$.env"
OVERRIDE_FILE=".verify.compose.$$.yml"

cleanup() {
  "${DOCKER_BIN}" compose -p "${PROJECT_NAME}" --env-file "${ENV_FILE}" -f docker-compose.yml -f "${OVERRIDE_FILE}" down -v --remove-orphans >/dev/null 2>&1 || true
  rm -f "${ENV_FILE}" "${OVERRIDE_FILE}"
}
trap cleanup EXIT

cat > "${ENV_FILE}" <<EOF
DB_ROOT_PASSWORD=ci-root-password-not-used-in-production
DB_USER=schoolmusic
DB_PASSWORD=ci-database-password-not-used-in-production
REDIS_PASSWORD=ci-redis-password-not-used-in-production
JWT_KEY=ci-jwt-key-with-at-least-32-bytes-not-production
AUDIO_LIBVLC_OUTPUT=dummy
AUTH_EMAIL_LOGIN_ENABLED=true
AUTH_PUBLIC_BASE_URL=http://localhost:${API_PORT}
SMTP_ENABLED=false
EOF

cat > "${OVERRIDE_FILE}" <<EOF
services:
  api:
    ports: !override
      - "${API_PORT}:8080"
EOF

echo "[verify-compose] Starting clean-volume MariaDB + Redis + API stack (${PROJECT_NAME})..."
if ! "${DOCKER_BIN}" compose -p "${PROJECT_NAME}" --env-file "${ENV_FILE}" -f docker-compose.yml -f "${OVERRIDE_FILE}" up -d --build --wait; then
  echo "[verify-compose] Stack failed to start cleanly. Dumping container status and logs:"
  "${DOCKER_BIN}" compose -p "${PROJECT_NAME}" --env-file "${ENV_FILE}" -f docker-compose.yml -f "${OVERRIDE_FILE}" ps -a || true
  "${DOCKER_BIN}" compose -p "${PROJECT_NAME}" --env-file "${ENV_FILE}" -f docker-compose.yml -f "${OVERRIDE_FILE}" logs --no-color --tail=200 || true
  exit 1
fi

echo "[verify-compose] Verifying MariaDB schema & seeded views..."
"${DOCKER_BIN}" compose -p "${PROJECT_NAME}" --env-file "${ENV_FILE}" -f docker-compose.yml -f "${OVERRIDE_FILE}" exec -T db \
  mysql -u schoolmusic -pci-database-password-not-used-in-production schoolmusic -e \
  "SELECT TABLE_NAME, TABLE_TYPE FROM information_schema.TABLES WHERE TABLE_SCHEMA = 'schoolmusic' AND TABLE_NAME IN ('users','refresh_tokens','password_reset_tokens','email_verification_tokens','spotify_playback_connection','song_vote_counts') ORDER BY TABLE_NAME; SELECT COUNT(*) AS seeded_songs FROM songs; SELECT COUNT(*) AS vote_view_rows FROM song_vote_counts;"

echo "[verify-compose] Waiting for API on http://127.0.0.1:${API_PORT}..."
ready=0
for _ in $(seq 1 45); do
  if curl -fsS "http://127.0.0.1:${API_PORT}/api/system/status" >/dev/null 2>&1; then
    ready=1
    break
  fi
  sleep 1
done

if [ "${ready}" != "1" ]; then
  echo "[verify-compose] API failed to become ready within 45s. Dumping container logs:"
  "${DOCKER_BIN}" compose -p "${PROJECT_NAME}" --env-file "${ENV_FILE}" -f docker-compose.yml -f "${OVERRIDE_FILE}" ps
  "${DOCKER_BIN}" compose -p "${PROJECT_NAME}" --env-file "${ENV_FILE}" -f docker-compose.yml -f "${OVERRIDE_FILE}" logs --no-color --tail=200
  exit 1
fi

for endpoint in "/api/system/status" "/api/queue/voting-target" "/api/tracks" "/api/queue" "/api/auth/capabilities"; do
  status_code="$(curl -sS -o /dev/null -w "%{http_code}" "http://127.0.0.1:${API_PORT}${endpoint}")"
  echo "  GET ${endpoint} -> HTTP ${status_code}"
  test "${status_code}" = "200"
done

callback_code="$(curl -sS -o /dev/null -w "%{http_code}" "http://127.0.0.1:${API_PORT}/api/spotify-playback/callback")"
echo "  GET /api/spotify-playback/callback -> HTTP ${callback_code}"
test "${callback_code}" = "400"

echo "[verify-compose] Clean-volume Docker Compose verification passed!"
