#!/usr/bin/env bash
# Backup of a running train_lang.sh run to a private HF model repository, through this machine (the GPU server has no HF token):
# every $EVERY seconds the newest resumable checkpoint (ckpt-latest.pt: weights + optimizer + data-stream state), the fine-tuning
# checkpoint, metrics, quality evaluations and logs are copied from the server and uploaded under backup/<name>/ (overwritten).
# Stops when export.done appears on the server (after one last upload).
#
#   NAME=go-100m TARGET=root@161.104.58.239 W=/root/flc-go100 RUN=go102m-ours bash hf_backup.sh
set -uo pipefail
NAME=${NAME:?}; TARGET=${TARGET:?}; W=${W:?}; RUN=${RUN:?}
REPO=${REPO:-dvislobokov/flc-models}
EVERY=${EVERY:-2700}
PY=${PY:-/tmp/claude-1000/-data-dataset-test/ac4e205a-34af-4d57-b6f5-ad2246bc7f41/scratchpad/hfvenv/bin/python}
L=${L:-/data/flc-backup/$NAME}
mkdir -p "$L"
log() { echo "{\"ts\": \"$(date +%H:%M:%S)\", \"event\": \"$1\"}"; }
while true; do
  last=0; ssh -o BatchMode=yes "$TARGET" "test -f $W/export.done" </dev/null && last=1
  rsync -a --timeout=600 -e "ssh -o BatchMode=yes" \
      --include="runs/" --include="runs/$RUN/" --include="runs/$RUN-ft/" \
      --include="runs/$RUN/ckpt-latest.pt" --include="runs/$RUN/metrics.jsonl" --include="runs/$RUN/config.json" \
      --include="runs/$RUN-ft/ckpt-latest.pt" --include="runs/$RUN-ft/metrics.jsonl" \
      --include="progress_eval.jsonl" --include="report.json" --include="*.log" --include="*.done" --include="*.cml" \
      --exclude="*" "$TARGET:$W/" "$L/" && log synced || log sync_failed
  "$PY" - "$L" "$REPO" "backup/$NAME" <<'PY' && log uploaded || log upload_failed
import os, sys
from huggingface_hub import HfApi
local, repo, path = sys.argv[1:4]
HfApi(token=open(os.path.expanduser("~/HF_TOKEN")).read().strip()).upload_folder(
    folder_path=local, repo_id=repo, repo_type="model", path_in_repo=path, commit_message=f"backup {path}")
PY
  [ "$last" = 1 ] && { log finished; break; }
  sleep "$EVERY"
done
