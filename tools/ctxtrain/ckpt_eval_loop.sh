#!/usr/bin/env bash
# Quality during training: every kept checkpoint (train.py --keep-every, runs/<run>/ckpt-<step>.pt) is evaluated on the
# first $N clean eval positions (eval_ctx.py: rest-of-line exact, shown, precision at the plugin gate); the shipped plugin
# model once as the reference. One line per checkpoint in $W/progress_eval.jsonl (watch_training.sh shows them).
# Runs next to the training on the same GPU (~1 min per checkpoint). Stops when train_lang.sh has finished the fine-tuning.
#
#   LANG_=go bash ckpt_eval_loop.sh
set -uo pipefail
LANG_=${LANG_:?go or csharp}
W=${W:-$HOME/flc-$LANG_}
N=${N:-2000}
DEV=${DEV:-cuda:0}
HERE=$(cd "$(dirname "$0")" && pwd)
if [ "$LANG_" = go ]; then NAME=go-16384; OLD=go-nn-50m-e3-lr2e3.cml; else NAME=cs-16384; OLD=cs-nn-50m-e3-lr2e3.cml; fi
RUN=${RUN:-${LANG_}${SIZE:-50m}-ours}
PY=$W/venv/bin/python
OUT=$W/progress_eval.jsonl
mkdir -p "$W/peval"
evaluate() {  # evaluate <name> <model path> <step>
  "$PY" "$HERE/eval_ctx.py" --engine "$W/engine" --vocab "$W/data/engine/$NAME/$NAME.bpe" \
      --positions "$W/data/caret/$NAME/eval/positions.jsonl" --limit "$N" --model "$1=$2:none" \
      --out "$W/peval/$1.json" --device "$DEV" --batch 128 > "$W/peval/$1.log" 2>&1 || return 1
  "$PY" - "$W/peval/$1.json" "$1" "$3" >> "$OUT" <<'PY'
import json, sys
r = json.load(open(sys.argv[1]))["runs"]
k = next(iter(r))
print(json.dumps({"model": sys.argv[2], "step": int(sys.argv[3]), **r[k]}))
PY
}
[ -f "$W/peval/old.json" ] || evaluate old "$W/engine/models/$OLD" 0
while true; do
  # language model checkpoints (step<N>), then the caret fine-tuning ones (ft<N>); the final models are evaluated by train_lang.sh
  for pair in "step:$W/runs/$RUN" "ft:$W/runs/$RUN-ft"; do
    tag=${pair%%:*}; dir=${pair#*:}
    for ck in $(ls "$dir"/ckpt-[0-9]*.pt 2>/dev/null | sort -V); do
      step=$(basename "$ck" .pt); step=${step#ckpt-}
      [ -f "$W/peval/$tag$step.json" ] || [ -f "$W/peval/$tag$step.failed" ] && continue
      sleep 30                                 # let the trainer finish writing the file
      evaluate "$tag$step" "$ck" "$step" || touch "$W/peval/$tag$step.failed"
    done
  done
  [ -f "$W/finetune.done" ] && break
  sleep 120
done
