#!/usr/bin/env bash
# Copies every snapshot of the running trainings on the GPU server to this machine with a bandwidth limit (a full-speed copy
# saturated the local link once): checkpoints (language model and fine-tuning, kept and latest), metrics, quality evaluations,
# reports, logs and exported .cml. Every $EVERY seconds until $STOP exists on the server, then a last copy.
#
#   bash sync_snapshots.sh            # defaults: the Go 100M run and the v2 re-fine-tuning queue
set -uo pipefail
TARGET=${TARGET:-root@161.104.58.239}
DEST=${DEST:-/data/flc-snapshots}
EVERY=${EVERY:-1200}
BWLIMIT=${BWLIMIT:-5000}                        # KB/s
STOP=${STOP:-/root/v2/go50/export.done}
SOURCES=${SOURCES:-"/root/flc-go100:go100 /root/v2:v2"}
log() { echo "{\"ts\": \"$(date +%H:%M:%S)\", \"event\": \"$1\"}"; }
while true; do
  last=0; ssh -o BatchMode=yes "$TARGET" "test -f $STOP" </dev/null && last=1
  for s in $SOURCES; do
    src=${s%%:*}; name=${s#*:}; mkdir -p "$DEST/$name"
    rsync -a --bwlimit="$BWLIMIT" --timeout=900 -e "ssh -o BatchMode=yes" \
        --exclude="data" --exclude="engine" --exclude="venv" --exclude="*.tmp" --exclude="*.samples.jsonl" \
        "$TARGET:$src/" "$DEST/$name/" && log "synced_$name" || log "sync_failed_$name"
  done
  [ "$last" = 1 ] && { log finished; break; }
  sleep "$EVERY"
done
