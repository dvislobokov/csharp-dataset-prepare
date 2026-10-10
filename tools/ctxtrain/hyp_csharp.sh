#!/usr/bin/env bash
# Two C# fine-tuning hypotheses on a spare GPU, both from the same language model (csharp-50m-ours-base.pt), run side by side:
#   noctx5: caret documents without context, lr 5e-5 (vs the shipped candidate: lr 2e-4)
#   ctx2:   caret documents with the context block, lr 2e-4 (same recipe as the candidate, only the context differs)
# Kept checkpoints every 4000 steps are evaluated on 2000 clean positions (progress_eval.jsonl); at the end both final
# models and the candidate (csharp-50m-ours-ft.pt) on all 12 000 positions with paired bootstrap -> report-hyp.json.
#
#   W=~/flc-cs bash hyp_csharp.sh      (needs $W/base.pt and $W/candidate.pt copied in beforehand)
set -uo pipefail
W=${W:-$HOME/flc-cs}
HERE=$(cd "$(dirname "$0")" && pwd)
PY=$W/venv/bin/python
D=$W/data/caret/cs-16384
VOCAB=$W/engine/models/cs-16384.bpe
log() { echo "{\"ts\": \"$(date -u +%H:%M:%S)\", \"event\": \"$1\"}"; }
cd "$W"
if [ ! -f data.done ]; then
  "$PY" - <<'PY'
from huggingface_hub import snapshot_download
snapshot_download("dvislobokov/csharp-ml-complation", repo_type="dataset", local_dir="data", allow_patterns=["caret/cs-16384/*"])
PY
  touch data.done
fi
log data_ok
ft() {  # ft <variant> <lr> <name>
  "$PY" "$HERE/finetune.py" --engine "$W/engine" --init "$W/base.pt" --docs "$D/train" --variant "$1" --out "$W/runs/$3" \
      --device cuda:0 --micro 32 --accum 1 --lr "$2" --warmup 200 --save-every 4000 --compile > "$W/$3.log" 2>&1
}
evaluate() {  # evaluate <tag> <ckpt> <step>: 2000 positions, mode none
  "$PY" "$HERE/eval_ctx.py" --engine "$W/engine" --vocab "$VOCAB" --positions "$D/eval/positions.jsonl" --limit 2000 \
      --model "$1=$2:none" --out "$W/peval/$1.json" --device cuda:0 --batch 128 > "$W/peval/$1.log" 2>&1 || return 1
  "$PY" - "$W/peval/$1.json" "$1" "$3" >> "$W/progress_eval.jsonl" <<'PY'
import json, sys
r = json.load(open(sys.argv[1]))["runs"]; k = next(iter(r))
print(json.dumps({"model": sys.argv[2], "step": int(sys.argv[3]), **r[k]}))
PY
}
mkdir -p runs peval
ft noctx 5e-5 noctx5 & a=$!
ft ctx 2e-4 ctx2 & b=$!
log started
while kill -0 $a 2>/dev/null || kill -0 $b 2>/dev/null; do
  for name in noctx5 ctx2; do
    for ck in $(ls "$W/runs/$name"/ckpt-[0-9]*.pt 2>/dev/null | sort -V); do
      s=$(basename "$ck" .pt); s=${s#ckpt-}
      [ -f "$W/peval/$name-$s.json" ] || [ -f "$W/peval/$name-$s.failed" ] && continue
      sleep 20; evaluate "$name-$s" "$ck" "$s" || touch "$W/peval/$name-$s.failed"
    done
  done
  sleep 60
done
log finetunes_done
"$PY" "$HERE/eval_ctx.py" --engine "$W/engine" --vocab "$VOCAB" --positions "$D/eval/positions.jsonl" \
    --model "candidate=$W/candidate.pt:none" --model "noctx5=$W/runs/noctx5/ckpt-latest.pt:none" \
    --model "ctx2=$W/runs/ctx2/ckpt-latest.pt:none,deps,full" --out "$W/report-hyp.json" --device cuda:0 --batch 128 > "$W/eval-hyp.log" 2>&1
log finished
