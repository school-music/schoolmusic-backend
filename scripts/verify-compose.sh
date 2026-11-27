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

for auth_page in "/reset-password?token=smoke-check" "/verify-email?token=smoke-check"; do
  page_code="$(curl -sS -o /dev/null -w "%{http_code}" "http://127.0.0.1:${API_PORT}${auth_page}")"
  echo "  GET ${auth_page} -> HTTP ${page_code}"
  test "${page_code}" = "200"
done

echo "[verify-compose] Verifying MariaDB login, refresh rotation, reuse detection, and logout..."
extract_json_field() {
  printf '%s' "$1" | sed -n "s/.*\"$2\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p"
}

login_json="$(curl -fsS -X POST "http://127.0.0.1:${API_PORT}/api/auth/login" \
  -H "Content-Type: application/json" \
  -d '{"name":"uczen","password":"Uczen123!"}')"
refresh_token_1="$(extract_json_field "${login_json}" "refreshToken")"
test -n "${refresh_token_1}"

refresh_json="$(curl -fsS -X POST "http://127.0.0.1:${API_PORT}/api/auth/refresh" \
  -H "Content-Type: application/json" \
  -d "{\"refreshToken\":\"${refresh_token_1}\"}")"
refresh_token_2="$(extract_json_field "${refresh_json}" "refreshToken")"
test -n "${refresh_token_2}"
test "${refresh_token_1}" != "${refresh_token_2}"
echo "  POST /api/auth/refresh -> HTTP 200 (rotated token)"

reuse_code="$(curl -sS -o /dev/null -w "%{http_code}" -X POST "http://127.0.0.1:${API_PORT}/api/auth/refresh" \
  -H "Content-Type: application/json" \
  -d "{\"refreshToken\":\"${refresh_token_1}\"}")"
echo "  POST /api/auth/refresh (reused old token) -> HTTP ${reuse_code}"
test "${reuse_code}" = "401"

revoked_family_code="$(curl -sS -o /dev/null -w "%{http_code}" -X POST "http://127.0.0.1:${API_PORT}/api/auth/refresh" \
  -H "Content-Type: application/json" \
  -d "{\"refreshToken\":\"${refresh_token_2}\"}")"
echo "  POST /api/auth/refresh (revoked family token) -> HTTP ${revoked_family_code}"
test "${revoked_family_code}" = "401"

login2_json="$(curl -fsS -X POST "http://127.0.0.1:${API_PORT}/api/auth/login" \
  -H "Content-Type: application/json" \
  -d '{"name":"uczen","password":"Uczen123!"}')"
access_token_2="$(extract_json_field "${login2_json}" "jwtSecurityToken")"
refresh_token_3="$(extract_json_field "${login2_json}" "refreshToken")"
test -n "${access_token_2}"
test -n "${refresh_token_3}"

logout_code="$(curl -sS -o /dev/null -w "%{http_code}" -X POST "http://127.0.0.1:${API_PORT}/api/auth/logout" \
  -H "Authorization: Bearer ${access_token_2}" \
  -H "Content-Type: application/json" \
  -d "{\"refreshToken\":\"${refresh_token_3}\"}")"
echo "  POST /api/auth/logout -> HTTP ${logout_code}"
test "${logout_code}" = "204"

me_after_logout_code="$(curl -sS -o /dev/null -w "%{http_code}" "http://127.0.0.1:${API_PORT}/api/auth/me" \
  -H "Authorization: Bearer ${access_token_2}")"
echo "  GET /api/auth/me (after logout) -> HTTP ${me_after_logout_code}"
test "${me_after_logout_code}" = "401"

refresh_after_logout_code="$(curl -sS -o /dev/null -w "%{http_code}" -X POST "http://127.0.0.1:${API_PORT}/api/auth/refresh" \
  -H "Content-Type: application/json" \
  -d "{\"refreshToken\":\"${refresh_token_3}\"}")"
echo "  POST /api/auth/refresh (after logout) -> HTTP ${refresh_after_logout_code}"
test "${refresh_after_logout_code}" = "401"

echo "[verify-compose] Clean-volume Docker Compose verification passed!"
