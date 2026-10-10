#!/usr/bin/env bash
# Keep the language-only model (before the caret fine-tuning) of a train_lang.sh run: wait for pretrain.done on the GPU
# server, export runs/<run>/ckpt-latest.pt to .cml there, and copy the .cml, the PyTorch checkpoint (without optimizer
# state: enough to fine-tune or export again) and the training metrics to a local directory.
#
#   bash save_base.sh <ssh target> "<ssh options>" <remote work dir> <lang> [local dir]
#   bash save_base.sh root@161.104.58.239 "" /root/flc-csharp csharp
set -euo pipefail
TARGET=$1; OPTS=$2; W=$3; LANG_=$4; DEST=${5:-/data/flc-models}
RUN=${LANG_}50m-ours
NAME=$([ "$LANG_" = go ] && echo go-16384 || echo cs-16384)
mkdir -p "$DEST"
# shellcheck disable=SC2086
r() { ssh -o BatchMode=yes $OPTS "$TARGET" "$@" </dev/null; }
until r "test -f $W/pretrain.done"; do sleep 60; done
r "cd $W/engine/tools/nn/train && $W/venv/bin/python export.py --ckpt $W/runs/$RUN/ckpt-latest.pt \
     --out $W/$LANG_-nn-50m-ours-base.cml --vocab $W/data/engine/$NAME/$NAME.bpe --lang $LANG_ > $W/export-base.log 2>&1 && \
   $W/venv/bin/python -c \"import torch; ck = torch.load('$W/runs/$RUN/ckpt-latest.pt', map_location='cpu', weights_only=False); \
     ck.pop('optimizer', None); ck.pop('stream', None); torch.save(ck, '$W/$LANG_-50m-ours-base.pt')\""
# shellcheck disable=SC2086
scp -q $OPTS "$TARGET:$W/$LANG_-nn-50m-ours-base.cml" "$TARGET:$W/$LANG_-50m-ours-base.pt" \
    "$TARGET:$W/runs/$RUN/metrics.jsonl" "$DEST/"
mv "$DEST/metrics.jsonl" "$DEST/$LANG_-50m-ours-base.metrics.jsonl"
(cd "$DEST" && sha256sum "$LANG_-nn-50m-ours-base.cml" "$LANG_-50m-ours-base.pt" | tee "$LANG_-base.sha256")
echo "saved to $DEST"
