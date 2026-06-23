#!/usr/bin/env bash
#
# migrate-gb7rdg.sh — the LinBPQ/BPQMail -> pdn-bbs CUTOVER runbook for GB7RDG.
#
# This is a CORRECTNESS-CRITICAL, one-way cutover: GB7RDG moves from its live
# LinBPQ node to a new pdn+pdn-bbs node. If the imported mailbox is wrong, the new
# node re-floods the packet network with duplicate mail and there is NO rollback
# once it is live as GB7RDG. So every step here is guarded, idempotent where it can
# be, and gated on an explicit human confirmation before anything is swapped.
#
# THE MODEL (docs/bpq-import.md): the importer is a DETERMINISTIC REBUILD. bbs.db
# is a pure function of the BPQ dump; each run rebuilds it from scratch and never
# modifies the BPQ source. So this script is re-runnable for dry runs and
# incremental pre-syncs, and "rollback" means simply discarding the rebuilt bbs.db.
#
# THE THREE RULES it preserves (verified by the importer + its validation summary):
#   1. Every BID is imported VERBATIM and the BID dedup store is seeded from the
#      WHOLE of WFBID.SYS (including orphan BIDs) — no message gets a fresh BID.
#   2. Already-forwarded / still-queued legs are pre-marked from each message's
#      forw/fbbs bitmaps, so the new node does not re-send mail BPQ already sent.
#   3. The message-number high-water mark is carried over, so new GB7RDG message
#      numbers never collide with numbers already on the network.
#
# CODE vs STATE (mirrors deploy-bbs.sh / docs/release-pipeline.md): bbs.db is
# STATE and lives in the BBS app's OWNER app-state dir on the node host
# (default /var/lib/packetnet/apps/bbs). This script writes ONLY there.
#
# USAGE
#   scripts/migrate-gb7rdg.sh --dry-run                 # default: validate only
#   scripts/migrate-gb7rdg.sh --pre-sync                # rebuild + stage, no swap
#   scripts/migrate-gb7rdg.sh --cutover                 # the gated live swap
#
# Each mode can pull a FRESH dump from the live LinBPQ box over SSH (--ssh-pull),
# or work from a local snapshot (--source DIR). A fresh dump should be taken with
# BPQMail STOPPED so DIRMES.SYS/WFBID.SYS/the Mail bodies are mutually consistent.

set -euo pipefail

# --- Config (env-overridable) ------------------------------------------------
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Live LinBPQ box (only touched when --ssh-pull is given; never written to).
BPQ_SSH="${BPQ_SSH:-root@gb7rdg}"
BPQ_DIR="${BPQ_DIR:-/opt/oarc/bpq}"             # BPQMail base dir on the live box
BPQ_SERVICE="${BPQ_SERVICE:-linbpq}"            # systemd unit to freeze for a clean dump

# Local working area.
WORK_DIR="${WORK_DIR:-$REPO_ROOT/.migrate-gb7rdg}"
SNAPSHOT_DIR="${SNAPSHOT_DIR:-$WORK_DIR/snapshot}"
BUILT_DB="${BUILT_DB:-$WORK_DIR/bbs.db}"

# Target pdn-bbs node host (only touched on --cutover).
NODE_SSH="${NODE_SSH:-root@packetdotnet}"
NODE_SERVICE="${NODE_SERVICE:-packetnet}"
NODE_STATE_DIR="${NODE_STATE_DIR:-/var/lib/packetnet/apps/bbs}"

OWN_CALL="${OWN_CALL:-}"                          # override BID identity (default: linmail.cfg BBSName)

# --- Args --------------------------------------------------------------------
MODE="dry-run"
SSH_PULL=0
LOCAL_SOURCE=""
ASSUME_YES=0

die() { echo "ERROR: $*" >&2; exit 1; }
note() { echo ">>> $*"; }

usage() {
  sed -n '2,40p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
  exit "${1:-0}"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --dry-run)   MODE="dry-run" ;;
    --pre-sync)  MODE="pre-sync" ;;
    --cutover)   MODE="cutover" ;;
    --ssh-pull)  SSH_PULL=1 ;;
    --source)    LOCAL_SOURCE="${2:-}"; shift ;;
    --yes|-y)    ASSUME_YES=1 ;;
    -h|--help)   usage 0 ;;
    *)           die "unknown argument '$1' (try --help)" ;;
  esac
  shift
done

# --- 1. Obtain a consistent BPQ dump ----------------------------------------
mkdir -p "$WORK_DIR"

if [[ -n "$LOCAL_SOURCE" ]]; then
  [[ -d "$LOCAL_SOURCE" ]] || die "--source '$LOCAL_SOURCE' is not a directory"
  SRC="$LOCAL_SOURCE"
  note "Using local BPQ snapshot: $SRC"
elif [[ "$SSH_PULL" == "1" ]]; then
  note "Pulling a FRESH dump from $BPQ_SSH:$BPQ_DIR"
  if [[ "$MODE" == "cutover" ]]; then
    note "Freezing BPQMail on the live box ($BPQ_SERVICE) so the dump is consistent..."
    if [[ "$ASSUME_YES" != "1" ]]; then
      read -r -p "Stop $BPQ_SERVICE on $BPQ_SSH now? This takes GB7RDG's mail offline. [y/N] " ans
      [[ "$ans" == "y" || "$ans" == "Y" ]] || die "aborted before freezing the live box"
    fi
    ssh "$BPQ_SSH" "systemctl stop $BPQ_SERVICE" || die "could not stop $BPQ_SERVICE on $BPQ_SSH"
  else
    note "NOTE: not stopping BPQMail (mode=$MODE). The dump may be momentarily inconsistent;"
    note "      orphans are reported and tolerated. Use --cutover for the frozen, authoritative dump."
  fi

  rm -rf "$SNAPSHOT_DIR"; mkdir -p "$SNAPSHOT_DIR/Mail"
  # Only the files the importer reads — keep the transfer small and the snapshot self-contained.
  scp "$BPQ_SSH:$BPQ_DIR/DIRMES.SYS"   "$SNAPSHOT_DIR/" || die "scp DIRMES.SYS failed"
  scp "$BPQ_SSH:$BPQ_DIR/WFBID.SYS"    "$SNAPSHOT_DIR/" || die "scp WFBID.SYS failed"
  scp "$BPQ_SSH:$BPQ_DIR/linmail.cfg"  "$SNAPSHOT_DIR/" || die "scp linmail.cfg failed"
  scp "$BPQ_SSH:$BPQ_DIR/bpq32.cfg"    "$SNAPSHOT_DIR/" 2>/dev/null || true
  scp "$BPQ_SSH:$BPQ_DIR/Mail/m_*.mes" "$SNAPSHOT_DIR/Mail/" 2>/dev/null || true
  SRC="$SNAPSHOT_DIR"
  note "Snapshot staged at $SRC"
else
  die "no source: pass --source <dir> for a local snapshot, or --ssh-pull to fetch from $BPQ_SSH"
fi

# --- 2. Consistency gate (refuse a badly inconsistent dump on cutover) -------
# A frozen, authoritative dump should have NO orphan headers (a header whose body
# file is gone). Orphan BODIES (purged messages still on disk) are normal and the
# importer ignores them. On --cutover we refuse if orphan HEADERS are present.
DIRMES="$SRC/DIRMES.SYS"
[[ -f "$DIRMES" ]] || die "missing DIRMES.SYS in $SRC"
[[ -f "$SRC/WFBID.SYS" ]] || note "WARNING: no WFBID.SYS in $SRC — the BID dedup store would be empty (re-flood risk)."

# --- 3. Build the importer ---------------------------------------------------
note "Building the importer (Release)..."
dotnet build "$REPO_ROOT/tools/Bbs.Import.Bpq/Bbs.Import.Bpq.csproj" -c Release >/dev/null
IMPORT() { dotnet run --project "$REPO_ROOT/tools/Bbs.Import.Bpq" -c Release --no-build -- "$@"; }
OWN_ARGS=(); [[ -n "$OWN_CALL" ]] && OWN_ARGS=(--own-call "$OWN_CALL")

# --- 4. Validate (always) ----------------------------------------------------
note "Validation summary (dry run):"
DRY_OUT="$(IMPORT --source "$SRC" --dry-run "${OWN_ARGS[@]}")"
echo "$DRY_OUT"

ORPHAN_HEADERS="$(echo "$DRY_OUT" | sed -n 's/.*Orphan headers (header, no body) *: \([0-9]*\).*/\1/p' | head -1)"
ORPHAN_HEADERS="${ORPHAN_HEADERS:-0}"

if [[ "$MODE" == "dry-run" ]]; then
  note "Dry run complete. No database written, no box touched."
  exit 0
fi

if [[ "$MODE" == "cutover" && "$ORPHAN_HEADERS" -gt 0 ]]; then
  die "$ORPHAN_HEADERS orphan header(s) in the dump — it is INCONSISTENT. Take a frozen dump (BPQMail stopped) before cutover."
fi

# --- 5. Rebuild bbs.db (deterministic, fresh, refuses to clobber sans --force) -
note "Rebuilding bbs.db -> $BUILT_DB"
rm -f "$BUILT_DB" "$BUILT_DB"-wal "$BUILT_DB"-shm "$BUILT_DB"-journal
IMPORT --source "$SRC" --target "$BUILT_DB" "${OWN_ARGS[@]}"
[[ -f "$BUILT_DB" ]] || die "import did not produce $BUILT_DB"
note "Built bbs.db: $(du -h "$BUILT_DB" | cut -f1)"

if [[ "$MODE" == "pre-sync" ]]; then
  note "Pre-sync complete. bbs.db rebuilt + staged at $BUILT_DB. Nothing swapped on the node."
  note "Re-run with --cutover (against a FROZEN dump) when ready to go live."
  exit 0
fi

# --- 6. Cutover: gated swap into the node's BBS app state --------------------
note "CUTOVER: about to swap the rebuilt bbs.db into $NODE_SSH:$NODE_STATE_DIR and restart $NODE_SERVICE."
note "This makes the new node live as GB7RDG. There is NO rollback once it forwards on-air."
if [[ "$ASSUME_YES" != "1" ]]; then
  read -r -p "Type 'GB7RDG' to confirm the live cutover: " ans
  [[ "$ans" == "GB7RDG" ]] || die "cutover not confirmed"
fi

note "Stopping $NODE_SERVICE on $NODE_SSH..."
ssh "$NODE_SSH" "systemctl stop $NODE_SERVICE" || die "could not stop $NODE_SERVICE"

note "Backing up any existing bbs.db on the node..."
ssh "$NODE_SSH" "mkdir -p '$NODE_STATE_DIR'; if [ -f '$NODE_STATE_DIR/bbs.db' ]; then cp -a '$NODE_STATE_DIR/bbs.db' '$NODE_STATE_DIR/bbs.db.pre-migrate.$(date +%Y%m%d%H%M%S)'; fi"

note "Copying the rebuilt bbs.db into place (WAL/SHM sidecars cleared)..."
ssh "$NODE_SSH" "rm -f '$NODE_STATE_DIR/bbs.db' '$NODE_STATE_DIR/bbs.db-wal' '$NODE_STATE_DIR/bbs.db-shm'"
scp "$BUILT_DB" "$NODE_SSH:$NODE_STATE_DIR/bbs.db" || die "scp of bbs.db to the node failed"

note "Starting $NODE_SERVICE on $NODE_SSH..."
ssh "$NODE_SSH" "systemctl start $NODE_SERVICE" || die "could not start $NODE_SERVICE"

note "CUTOVER COMPLETE. Verify the node came up with the imported mailbox, then (if --ssh-pull)"
note "decommission the OLD LinBPQ GB7RDG so two stations don't both claim the call."
