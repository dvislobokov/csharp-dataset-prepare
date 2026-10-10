#!/usr/bin/env bash
# A plugin model trained from scratch on our data, end to end on a GPU machine (no secrets needed: the datasets are public):
#   1. language: the engine's train.py on engine/<vocab> (whole files, FIM), go50m preset, the engine's lr2e3 recipe,
#      DDP over all GPUs
#   2. task: finetune.py on caret/<vocab>/train (noctx variant, loss only on the completion)
#      + control (2+ GPUs or OLD_FT=1): the shipped plugin model fine-tuned on the same documents
#   3. eval_ctx.py on caret/<vocab>/eval (clean eval-fresh positions): new, new before step 2, shipped, shipped + step 2
#   4. export of the new model to .cml (engine export.py)
# Every step writes <step>.done in $W, so a rerun continues (train.py itself resumes from ckpt-latest.pt).
#
#   LANG_=go bash train_lang.sh          (APP = a copy of csharp-dataset-prepare/tools/ctxtrain next to this script)
set -euo pipefail
LANG_=${LANG_:?go or csharp}
W=${W:-$HOME/flc-$LANG_}
TOKENS=${TOKENS:-0}                       # 0 = one full epoch of the lm fold
HERE=$(cd "$(dirname "$0")" && pwd)
if [ "$LANG_" = go ]; then
  REPO=dvislobokov/go-ml-complation; NAME=go-16384; OLD=go-nn-50m-e3-lr2e3.cml
else
  REPO=dvislobokov/csharp-ml-complation; NAME=cs-16384; OLD=cs-nn-50m-e3-lr2e3.cml
fi
RUN=${RUN:-${LANG_}50m-ours}
mkdir -p "$W" && cd "$W"
log() { echo "{\"ts\": \"$(date -u +%H:%M:%S)\", \"event\": \"$1\"}"; }
step() { [ -f "$W/$1.done" ] && return 1; log "start_$1"; return 0; }
done_() { touch "$W/$1.done"; log "done_$1"; }

if step setup; then
  command -v python3 >/dev/null
  python3 -m venv venv 2>/dev/null || { sudo apt-get install -y -qq python3-venv >/dev/null; python3 -m venv venv; }
  venv/bin/pip install -q torch numpy pyarrow huggingface_hub
  [ -d engine ] || git clone -q --depth 1 https://github.com/dvislobokov/idea-ml-completion.git engine
  venv/bin/python - "$REPO" "$NAME" <<'PY'
import sys
from huggingface_hub import snapshot_download
repo, name = sys.argv[1:3]
snapshot_download(repo, repo_type="dataset", local_dir="data", allow_patterns=[f"engine/{name}/*", f"caret/{name}/*"])
PY
  done_ setup
fi
PY=$W/venv/bin/python
NGPU=$(nvidia-smi -L | wc -l)
DATA=$W/data/engine/$NAME
CARET=$W/data/caret/$NAME
T=$W/engine/tools/nn/train
log "gpus_$NGPU"

# 1. language model from scratch
if step pretrain; then
  max=$TOKENS
  [ "$max" = 0 ] && max=$("$PY" -c "import json; print(json.load(open('$DATA/lm.meta.json'))['tokens'])")
  (cd "$T" && "$PY" -m torch.distributed.run --standalone --nproc_per_node "$NGPU" train.py --preset go50m --run "$RUN" \
      --out "$W/runs" --data "$DATA" --vocab "$DATA/$NAME.bpe" --lr 2e-3 --tokens-per-step 524288 --micro-batch 32 \
      --warmup 500 --fim-rate 0.7 --spm-rate 0.5 --max-tokens "$max" --eval-every 1000 --ckpt-every 1000 --keep-every 5000 \
      --compile) > "$W/pretrain.log" 2>&1
  done_ pretrain
fi
BASE=$W/runs/$RUN/ckpt-latest.pt

# 2. caret fine-tuning (and the control on a second GPU)
if step finetune; then
  "$PY" "$HERE/finetune.py" --engine "$W/engine" --init "$BASE" --docs "$CARET/train" --variant noctx --out "$W/runs/$RUN-ft" \
      --device cuda:0 --micro 32 --accum 1 --lr 2e-4 --warmup 200 --compile > "$W/finetune.log" 2>&1 &
  a=$!
  if [ "$NGPU" -ge 2 ] || [ "${OLD_FT:-0}" = 1 ]; then
    dev=$([ "$NGPU" -ge 2 ] && echo cuda:1 || echo cuda:0)
    [ "$NGPU" -ge 2 ] || wait $a
    "$PY" "$HERE/finetune.py" --engine "$W/engine" --init "$W/engine/models/$OLD" --docs "$CARET/train" --variant noctx \
        --out "$W/runs/old-ft" --device "$dev" --micro 32 --accum 1 --lr 2e-4 --warmup 200 --compile > "$W/finetune-old.log" 2>&1
  fi
  wait $a
  done_ finetune
fi

# 3. evaluation on clean positions
if step eval; then
  models=(--model "new=$W/runs/$RUN-ft/ckpt-latest.pt:none" --model "new_base=$BASE:none" --model "old=$W/engine/models/$OLD:none")
  [ -f "$W/runs/old-ft/ckpt-latest.pt" ] && models+=(--model "old_ft=$W/runs/old-ft/ckpt-latest.pt:none")
  "$PY" "$HERE/eval_ctx.py" --engine "$W/engine" --vocab "$DATA/$NAME.bpe" --positions "$CARET/eval/positions.jsonl" \
      "${models[@]}" --out "$W/report.json" --device cuda:0 --batch 128 > "$W/eval.log" 2>&1
  done_ eval
fi

# 4. export for the plugin
if step export; then
  (cd "$T" && "$PY" export.py --ckpt "$W/runs/$RUN-ft/ckpt-latest.pt" --out "$W/$LANG_-nn-50m-ours-ft.cml" --vocab "$DATA/$NAME.bpe" \
      --lang "$([ "$LANG_" = go ] && echo go || echo csharp)") > "$W/export.log" 2>&1
  sha256sum "$W/$LANG_-nn-50m-ours-ft.cml"
  done_ export
fi
grep '"run"' "$W/eval.log" || true
log finished
