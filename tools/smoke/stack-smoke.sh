#!/usr/bin/env bash
# End-to-end smoke test for the docker-compose stack (SQL Server, Mosquitto, ForgeLink, ERP + OT simulators).
# Proves the production-like paths that unit/integration tests (SQLite, in-memory fakes) cannot:
#   SQL Server schema + stored procedures, MQTT ingestion from a real broker, outbox delivery to the ERP over HTTP,
#   the daily OEE rollup on SQL Server, and the API surface (docs, pagination) behind the container image.
#
# Usage:  docker compose up -d --build && tools/smoke/stack-smoke.sh
# Needs:  bash, curl, jq.  BASE defaults to http://localhost:5100.
set -euo pipefail

BASE="${BASE:-http://localhost:5100}"
DEVICE_KEY="${DEVICE_KEY:-dev-handheld-01}"
pass=0

ok()   { pass=$((pass + 1)); printf '  \033[32mPASS\033[0m %s\n' "$*"; }
fail() { printf '  \033[31mFAIL\033[0m %s\n' "$*"; exit 1; }

# Poll a command until it succeeds or the timeout (seconds) expires.
eventually() {
  local timeout="$1" what="$2"; shift 2
  local deadline=$((SECONDS + timeout))
  until "$@" >/dev/null 2>&1; do
    (( SECONDS < deadline )) || fail "$what (gave up after ${timeout}s)"
    sleep 3
  done
  ok "$what"
}

api() { curl -fsS -H "X-Api-Key: $DEVICE_KEY" "$@"; }
# Development auth: requests without a device key run as the dev supervisor (needed for supervisor-only endpoints).
supervisor() { curl -fsS "$@"; }

echo "ForgeLink stack smoke test against $BASE"

eventually 240 "web is ready (database created, SQL Server programmability deployed)" \
  curl -fsS "$BASE/health/ready"

[[ "$(curl -fsS "$BASE/openapi/v1.json" | jq -r .info.title)" == "ForgeLink API" ]] && ok "OpenAPI document served" || fail "OpenAPI document"
curl -fsS -o /dev/null "$BASE/docs/" && ok "API reference UI served" || fail "API reference UI"

machines_reporting() {
  api "$BASE/api/machines" | jq -e '[.[] | select(.state != "Offline")] | length > 0'
}
eventually 180 "OT simulator -> Mosquitto -> ForgeLink: machines report live state over MQTT" machines_reporting

counts_arriving() {
  api "$BASE/api/kpis/lines/L1" | jq -e '[.throughput[] | .good] | add > 0'
}
eventually 180 "line KPIs (SQL Server Pareto stored procedure) include simulated production" counts_arriving

# A material adjustment (supervisor-only; operators get 403 by design) is mirrored to the ERP through the outbox.
before=$(api "$BASE/api/integration/health" | jq .sentLastHour)
supervisor -X POST "$BASE/api/inventory/adjustments" \
  -H "Content-Type: application/json" -H "Idempotency-Key: smoke-$(date +%s)" \
  -d '{"sku":"PKG-TRAY-24","lotNumber":"","location":"FG-01","quantityDelta":1,"reasonCode":"FOUND"}' >/dev/null \
  && ok "inventory adjustment accepted (Idempotency-Key)" || fail "inventory adjustment"
outbox_delivered() {
  api "$BASE/api/integration/health" | jq -e --argjson b "$before" '.sentLastHour > $b'
}
eventually 120 "outbox delivered the adjustment to the ERP simulator (with 15% simulated failures)" outbox_delivered

rows=$(supervisor -X POST "$BASE/api/reports/oee/rollup" | jq .rowsWritten)
(( rows > 0 )) && ok "daily OEE rollup wrote $rows machine-days on SQL Server" || fail "rollup wrote no rows"
lines=$(api "$BASE/api/reports/oee/daily?format=csv" | wc -l)
(( lines > 1 )) && ok "daily OEE CSV export ($((lines - 1)) rows)" || fail "CSV export empty"

first=$(api "$BASE/api/inventory/ledger?limit=1")
cursor=$(jq -r .nextCursor <<<"$first")
[[ "$cursor" != "null" ]] || fail "ledger has only one row"
second=$(api "$BASE/api/inventory/ledger?limit=1&cursor=$cursor")
[[ "$(jq -r '.items[0].id' <<<"$first")" != "$(jq -r '.items[0].id' <<<"$second")" ]] && ok "ledger cursor pagination" || fail "ledger pagination repeated a row"

echo "All $pass checks passed."
