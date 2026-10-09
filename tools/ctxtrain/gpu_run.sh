#!/usr/bin/env bash
# The context experiment on a 2-GPU machine (docs/CONTEXT_SPEC-RU.md 10.3, README.md here):
#   A = shipped cs50m fine-tuned on caret documents WITH profile/facts (GPU 0)
#   B = the same documents with the context always removed — the control (GPU 1)
# then one evaluation of A (none/deps/full), B (none) and the shipped model (none) on the clean eval-fresh positions.
#
#   HF_TOKEN=... bash gpu_run.sh            # token from the environment, never on the command line
set -euo pipefail
W=${W:-$HOME/ctx}
REPO=${REPO:-dvislobokov/csharp-ml-complation}
DOCS=${DOCS:-ctxdocs/v1.1-cs50m}
mkdir -p "$W" && cd "$W"

# 1. environment (once)
if [ ! -d venv ]; then
  python3 -m venv venv
  venv/bin/pip install -q torch numpy pyarrow huggingface_hub
fi
[ -d engine ] || git clone -q --depth 1 https://github.com/dvislobokov/idea-ml-completion.git engine
[ -d app ] || git clone -q --depth 1 https://github.com/dvislobokov/csharp-dataset-prepare.git app
venv/bin/python - <<PY
import os
from huggingface_hub import snapshot_download
snapshot_download("$REPO", repo_type="dataset", token=os.environ.get("HF_TOKEN"), local_dir="data", allow_patterns=["$DOCS/*"])
PY
T=app/tools/ctxtrain
MODEL=engine/models/cs-nn-50m-e3-lr2e3.cml
VOCAB=engine/models/cs-16384.bpe
D=data/$DOCS

# 2. A and B in parallel, one GPU each, same documents/order/seed
for v in ctx noctx; do
  dev=$([ $v = ctx ] && echo cuda:0 || echo cuda:1)
  nohup venv/bin/python $T/finetune.py --engine engine --init $MODEL --docs $D/train --variant $v --out runs/$v \
      --device $dev --micro 16 --accum 2 --lr 2e-4 --compile > runs-$v.log 2>&1 &
done
wait
tail -n 2 runs-ctx.log runs-noctx.log

# 3. evaluation (one process: paired bootstrap needs all runs together)
venv/bin/python $T/eval_ctx.py --engine engine --vocab $VOCAB --positions $D/eval/positions.jsonl \
    --model A=runs/ctx/ckpt-latest.pt:none,deps,full --model B=runs/noctx/ckpt-latest.pt:none \
    --model old=$MODEL:none --out report.json --device cuda:0 --batch 96 | tee eval.log
echo "report: $W/report.json"
