#!/usr/bin/env bash
# Short caret fine-tuning runs to compare hyper-parameters (same tokens per run, cosine over the run), one after another on
# one GPU; each run is evaluated at its midpoint and end on the first 2000 clean positions -> $W/progress_eval.jsonl.
#
#   W=~/flc-cs BASE=~/flc-cs/base.pt DEV=cuda:0 bash hyp_lr.sh "lr1e4:1e-4:32:6000" "lr2e5:2e-5:32:6000"
#   (name:lr:micro:steps; micro 64 with 3000 steps sees the same documents as micro 32 with 6000)
set -uo pipefail
W=${W:?}; BASE=${BASE:?}; DEV=${DEV:-cuda:0}
HERE=$(cd "$(dirname "$0")" && pwd)
PY=$W/venv/bin/python
D=$W/data/caret/cs-16384
VOCAB=$W/engine/models/cs-16384.bpe
mkdir -p "$W/runs-short" "$W/peval"
evaluate() {  # evaluate <tag> <ckpt> <step>
  "$PY" "$HERE/eval_ctx.py" --engine "$W/engine" --vocab "$VOCAB" --positions "$D/eval/positions.jsonl" --limit 2000 \
      --model "$1=$2:none" --out "$W/peval/$1.json" --device "$DEV" --batch 128 > "$W/peval/$1.log" 2>&1 || return 1
  "$PY" - "$W/peval/$1.json" "$1" "$3" >> "$W/progress_eval.jsonl" <<'PY'
import json, sys
r = json.load(open(sys.argv[1]))["runs"]; k = next(iter(r))
print(json.dumps({"model": sys.argv[2], "step": int(sys.argv[3]), **r[k]}))
PY
}
for spec in "$@"; do
  IFS=: read -r name lr micro steps <<< "$spec"
  out=$W/runs-short/$name
  [ -f "$out/done" ] && continue
  "$PY" "$HERE/finetune.py" --engine "$W/engine" --init "$BASE" --docs "$D/train" --variant noctx --out "$out" --device "$DEV" \
      --micro "$micro" --accum 1 --lr "$lr" --warmup 200 --max-steps "$steps" --save-every $((steps / 2)) --compile \
      > "$out.log" 2>&1 || { echo "failed $name"; continue; }
  evaluate "$name-mid" "$out/ckpt-$((steps / 2)).pt" $((steps / 2))
  evaluate "$name-end" "$out/ckpt-latest.pt" "$steps"
  touch "$out/done"
  echo "{\"event\": \"run_done\", \"name\": \"$name\"}"
done
