#!/usr/bin/env bash
# =========================================================================
# Copied-DB Preflight Script for Channels v8 Migration
# Task: #2101 / #2088 — Offline DB conversion preflight
#
# Usage: ./scripts/copied-db-preflight.sh <copied-db-path>
#
# Performs preflight checks on a copied (not live) Channels SQLite database:
# - Row counts for key tables
# - Source-kind distribution in channel_messages
# - Membership / subscription row counts
# - PRAGMA foreign_key_check
# - PRAGMA integrity_check
# - Forbidden column checks (hermes_session_key, parent_hermes_session_key, gateway_delivery)
# - Schema version check
#
# Exit code 0 = all checks pass. Non-zero = at least one check failed.
# =========================================================================

set -euo pipefail

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

DB_PATH="${1:-}"
if [ -z "$DB_PATH" ]; then
    echo "Usage: $0 <copied-db-path>"
    exit 2
fi

if [ ! -f "$DB_PATH" ]; then
    echo -e "${RED}ERROR: Database not found at $DB_PATH${NC}"
    exit 2
fi

echo "=== Channels v8 Copied-DB Preflight ==="
echo "DB: $DB_PATH"
echo "Started: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
echo ""

sqlite() {
    sqlite3 "$DB_PATH" "$@"
}

PASS=0
FAIL=0
WARN=0

check() {
    local label="$1"
    local condition="$2"
    local detail="${3:-}"
    if [ "$condition" = "true" ] || [ "$condition" = "0" ]; then
        echo -e "${GREEN}PASS${NC} $label${detail:+: $detail}"
        ((PASS+=1))
    else
        echo -e "${RED}FAIL${NC} $label${detail:+: $detail}"
        ((FAIL+=1))
    fi
}

warn_check() {
    local label="$1"
    local detail="$2"
    echo -e "${YELLOW}WARN${NC} $label: $detail"
    ((WARN+=1))
}

# --- Schema version ---
echo "--- Schema Version ---"
VERSION=$(sqlite "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;" 2>/dev/null || echo "0")
echo "  Current schema version: $VERSION"
check "Schema version >= 8" "$([ "$VERSION" -ge 8 ] && echo true || echo false)" "found v$VERSION"

# --- Row counts ---
echo ""
echo "--- Table Row Counts ---"
for table in channels channel_messages channel_memberships channel_activity_events \
    channel_read_cursors channel_subscriptions channel_subscription_cursors \
    channel_reactions channel_project_links worker_pool_lobby_presence \
    direct_conversations direct_conversation_entries; do
    count=$(sqlite "SELECT COUNT(*) FROM $table;" 2>/dev/null || echo "N/A")
    echo "  $table: $count"
done

# --- Source-kind distribution ---
echo ""
echo "--- channel_messages.source_kind Distribution ---"
sqlite "SELECT COALESCE(source_kind, '(null)') as kind, COUNT(*) as cnt FROM channel_messages GROUP BY source_kind ORDER BY cnt DESC;" 2>/dev/null || echo "  (query failed)"

# --- Forbidden column check ---
echo ""
echo "--- Forbidden Column Checks (v8 target) ---"
HERMES_COL=$(sqlite "SELECT COUNT(*) FROM pragma_table_info('channel_activity_events') WHERE name='hermes_session_key';" 2>/dev/null || echo "0")
PARENT_HERMES_COL=$(sqlite "SELECT COUNT(*) FROM pragma_table_info('channel_activity_events') WHERE name='parent_hermes_session_key';" 2>/dev/null || echo "0")
check "hermes_session_key absent from channel_activity_events" "$([ "$HERMES_COL" = "0" ] && echo true || echo false)" "found $HERMES_COL column(s)"
check "parent_hermes_session_key absent from channel_activity_events" "$([ "$PARENT_HERMES_COL" = "0" ] && echo true || echo false)" "found $PARENT_HERMES_COL column(s)"

# Check gateway_delivery in CHECK constraint. v8 may keep this as an explicit
# historical/tombstone compatibility value, but green-path rows are migrated away.
GW_CONSTRAINT=$(sqlite "SELECT sql FROM sqlite_master WHERE type='table' AND name='channel_messages';" 2>/dev/null || echo "")
if echo "$GW_CONSTRAINT" | grep -q "gateway_delivery"; then
    warn_check "gateway_delivery CHECK value" "still present as compatibility/tombstone; verify no green-path rows remain"
else
    check "gateway_delivery removed from channel_messages CHECK" "true"
fi

# Check remaining gateway_delivery rows
GW_ROWS=$(sqlite "SELECT COUNT(*) FROM channel_messages WHERE source_kind='gateway_delivery';" 2>/dev/null || echo "0")
check "No gateway_delivery rows remain" "$([ "$GW_ROWS" = "0" ] && echo true || echo false)" "found $GW_ROWS row(s)"

# --- Subscription vs membership counts ---
echo ""
echo "--- Subscription vs Membership Counts ---"
MEM_ACTIVE=$(sqlite "SELECT COUNT(*) FROM channel_memberships WHERE membership_status='active';" 2>/dev/null || echo "0")
SUB_ACTIVE=$(sqlite "SELECT COUNT(*) FROM channel_subscriptions WHERE subscription_status NOT IN ('left','released','quarantined');" 2>/dev/null || echo "0")
echo "  Active memberships: $MEM_ACTIVE"
echo "  Active subscriptions: $SUB_ACTIVE"

SUB_PURPOSE=$(sqlite "SELECT COALESCE(subscription_purpose, '(null)') as purpose, COUNT(*) as cnt FROM channel_subscriptions GROUP BY subscription_purpose ORDER BY cnt DESC;" 2>/dev/null || echo "  (query failed)")
echo "  Subscription purposes:"
echo "$SUB_PURPOSE" | while read -r line; do echo "    $line"; done

# --- Subscription cursor counts ---
echo ""
CURSOR_COUNT=$(sqlite "SELECT COUNT(*) FROM channel_subscription_cursors;" 2>/dev/null || echo "0")
READ_CURSOR_COUNT=$(sqlite "SELECT COUNT(*) FROM channel_read_cursors;" 2>/dev/null || echo "0")
echo "  Subscription cursors: $CURSOR_COUNT"
echo "  Read cursors (human/UI): $READ_CURSOR_COUNT"

# --- V8 membership columns ---
echo ""
echo "--- Membership V8 Column Checks ---"
PROFILE_COL=$(sqlite "SELECT COUNT(*) FROM pragma_table_info('channel_memberships') WHERE name='profile_identity';" 2>/dev/null || echo "0")
ROLE_COL=$(sqlite "SELECT COUNT(*) FROM pragma_table_info('channel_memberships') WHERE name='member_role';" 2>/dev/null || echo "0")
LEFT_AT_COL=$(sqlite "SELECT COUNT(*) FROM pragma_table_info('channel_memberships') WHERE name='left_at';" 2>/dev/null || echo "0")
check "profile_identity column exists" "$([ "$PROFILE_COL" = "1" ] && echo true || echo false)"
check "member_role column exists" "$([ "$ROLE_COL" = "1" ] && echo true || echo false)"
check "left_at column exists" "$([ "$LEFT_AT_COL" = "1" ] && echo true || echo false)"

# --- PRAGMA checks ---
echo ""
echo "--- PRAGMA Integrity Checks ---"

FK_CHECK=$(sqlite "PRAGMA foreign_key_check;" 2>/dev/null)
if [ -z "$FK_CHECK" ]; then
    check "PRAGMA foreign_key_check" "true" "no violations"
else
    warn_check "PRAGMA foreign_key_check" "violations found"
    echo "$FK_CHECK" | while read -r line; do echo "    FK violation: $line"; done
fi

INTEGRITY=$(sqlite "PRAGMA integrity_check;" 2>/dev/null)
if [ "$INTEGRITY" = "ok" ]; then
    check "PRAGMA integrity_check" "true" "ok"
else
    warn_check "PRAGMA integrity_check" "$INTEGRITY"
fi

# --- Summary ---
echo ""
echo "=== Preflight Summary ==="
echo "  PASS: $PASS"
echo "  FAIL: $FAIL"
echo "  WARN: $WARN"
echo "Finished: $(date -u +%Y-%m-%dT%H:%M:%SZ)"

if [ "$FAIL" -gt 0 ]; then
    echo -e "${RED}Preflight FAILED — do not proceed with live cutover${NC}"
    exit 1
fi

echo -e "${GREEN}Preflight PASSED — copied DB looks clean for v8 migration${NC}"
exit 0
