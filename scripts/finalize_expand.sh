#!/usr/bin/env bash
# After an expansion run (scripts/flc_run.py / goflc_run.py finished): rebuild everything derived from the corpus and samples
# for one language and publish it to the language's HF dataset.
#   1. engine/<vocab>/   corpus encoded with the plugin engine's tokenizer (encode_engine_shards.py)
#   2. decontam/, eval-fresh/decontam/   near duplicates of held-out and eval-fresh files in train (near_dup.py)
#   3. deps/             dependency profiles (deps_profile.py; also for eval-fresh)
#   4. caret/<vocab>/train  ~1 G tokens of caret documents (make_docs.py; ctx and noctx variants)
#      caret/<vocab>/eval   clean eval-fresh positions minus near duplicates
#   5. checks (tools/ctxtrain/check_caret.py) and upload of caret/ and the decontam/deps folders
#
#   LANG_=csharp bash finalize_expand.sh        (C# server)      LANG_=go bash finalize_expand.sh   (Go server)
# Steps write a <step>.done marker in $W, so a rerun continues after the last finished step.
set -euo pipefail
LANG_=${LANG_:?csharp or go}
APP=${APP:-/srv/flc/app}
PY=${PY:-/srv/flc/venv-tok/bin/python}
ENGINE=${ENGINE:-/srv/flc/ctx/engine}
WORKERS=${WORKERS:-100}
DOCS=${DOCS:-1050000}
PER_REPO=${PER_REPO:-60}
if [ "$LANG_" = csharp ]; then
  HF=dvislobokov/csharp-ml-complation; VOCAB=$ENGINE/models/cs-16384.bpe; SH=${SH:-/srv/flc/engine-shards}
else
  HF=dvislobokov/go-ml-complation; VOCAB=$ENGINE/models/go-16384.bpe; SH=${SH:-/srv/flc/engine-shards-go}
fi
NAME=$(basename "$VOCAB" .bpe)
W=${W:-/srv/flc/finalize/$LANG_}
mkdir -p "$W"
log() { echo "{\"ts\": \"$(date -u +%H:%M:%S)\", \"lang\": \"$LANG_\", \"event\": \"$1\"}"; }
step() { [ -f "$W/$1.done" ] && { log "skip_$1"; return 1; }; log "start_$1"; return 0; }
done_() { touch "$W/$1.done"; log "done_$1"; }
upload() {  # upload <local dir> <path in repo> <message>
  "$PY" -I - "$HF" "$1" "$2" "$3" <<'PY'
import sys
from huggingface_hub import HfApi
repo, local, path, msg = sys.argv[1:5]
HfApi(token=open("/srv/flc/secrets/HF_TOKEN").read().strip()).upload_folder(
    folder_path=local, path_in_repo=path, repo_id=repo, repo_type="dataset", commit_message=msg)
PY
}

# 1. engine shards (downloads the whole corpus to $SH/src)
if step encode; then
  "$PY" -I "$APP/scripts/encode_engine_shards.py" --repo "$HF" --lang "$LANG_" --engine "$ENGINE/tools/nn/tokenizer" --vocab "$VOCAB" \
      --work "$SH" --workers "$WORKERS" --upload
  done_ encode
fi
CORPUS=$SH/src/data/corpus

# eval-fresh corpus (local copy)
FRESH=$W/fresh
if step fresh; then
  "$PY" -I - "$HF" "$FRESH" <<'PY'
import sys
from huggingface_hub import snapshot_download
snapshot_download(sys.argv[1], repo_type="dataset", local_dir=sys.argv[2], token=open("/srv/flc/secrets/HF_TOKEN").read().strip(),
                  allow_patterns=["eval-fresh/data/corpus/*.parquet"])
PY
  done_ fresh
fi
FRESH_CORPUS=$FRESH/eval-fresh/data/corpus

# 2. near duplicates: held-out splits and eval-fresh vs train
ND=$W/neardup
if step neardup; then
  "$PY" -I "$APP/scripts/near_dup.py" --corpus "$CORPUS" --extra-eval "$FRESH_CORPUS" --out "$ND" --workers "$WORKERS"
  mkdir -p "$ND/main" "$ND/fresh"
  "$PY" -I - "$ND" <<'PY'
import json, os, shutil, sys
nd = sys.argv[1]
with open(os.path.join(nd, "near_duplicates.jsonl")) as f, \
     open(os.path.join(nd, "main/near_duplicates.jsonl"), "w") as m, open(os.path.join(nd, "fresh/near_duplicates.jsonl"), "w") as e:
    for line in f:
        (e if json.loads(line)["set"].startswith("extra") else m).write(line)
s = json.load(open(os.path.join(nd, "summary.json")))
json.dump({**s, "sets": {k: v for k, v in s["sets"].items() if not k.startswith("extra")}}, open(os.path.join(nd, "main/summary.json"), "w"), indent=1)
json.dump({**s, "sets": {k: v for k, v in s["sets"].items() if k.startswith("extra")}}, open(os.path.join(nd, "fresh/summary.json"), "w"), indent=1)
for n in ("repos-validation.json", "repos-test.json"):
    shutil.copy(os.path.join(nd, n), os.path.join(nd, "main", n))
shutil.copy(os.path.join(nd, "repos-extra0_corpus.json"), os.path.join(nd, "fresh/repos-eval-fresh.json"))
PY
  upload "$ND/main" decontam "Near-duplicate decontamination after the corpus expansion"
  upload "$ND/fresh" eval-fresh/decontam "eval-fresh near duplicates against the expanded train corpus"
  done_ neardup
fi

# 3. dependency profiles (train corpus and eval-fresh)
if step deps; then
  "$PY" -I "$APP/scripts/deps_profile.py" --lang "$LANG_" --corpus "$CORPUS" --out "$W/deps" --workers "$WORKERS"
  "$PY" -I "$APP/scripts/deps_profile.py" --lang "$LANG_" --corpus "$FRESH_CORPUS" --out "$W/deps-fresh" --workers "$WORKERS"
  upload "$W/deps" deps "Dependency profiles after the corpus expansion"
  done_ deps
fi

# 4. caret documents
OUT=$W/caret/$NAME
if step docs_train; then
  rm -rf "$OUT/train"
  (cd "$APP/tools/ctxtrain" && "$PY" -I make_docs.py --lang "$LANG_" --hf-repo "$HF" --engine "$ENGINE" --vocab "$VOCAB" \
      --split train --docs "$DOCS" --per-repo "$PER_REPO" --deps-dir "$W/deps" --corpus-dir "$CORPUS" \
      --out "$OUT/train" --workers "$WORKERS")
  rm -rf "$OUT/train/hf"
  done_ docs_train
fi
if step docs_eval; then
  rm -rf "$OUT/eval"
  (cd "$APP/tools/ctxtrain" && "$PY" -I make_docs.py --lang "$LANG_" --hf-repo "$HF" --engine "$ENGINE" --vocab "$VOCAB" \
      --prefix eval-fresh/ --split all --eval --docs 12000 --per-repo 60 --deps-dir "$W/deps-fresh" --corpus-dir "$FRESH_CORPUS" \
      --exclude "$ND/fresh/near_duplicates.jsonl" --out "$OUT/eval" --workers "$WORKERS")
  rm -rf "$OUT/eval/hf"
  done_ docs_eval
fi

# 5. checks, then upload
if step check; then
  "$PY" -I "$APP/tools/ctxtrain/check_caret.py" --engine "$ENGINE" --vocab "$VOCAB" --docs "$OUT/train" --eval "$OUT/eval" \
      --corpus "$CORPUS" --fresh-corpus "$FRESH_CORPUS" --out "$OUT/CHECKS.json"
  done_ check
fi
if step upload; then
  upload "$OUT" "caret/$NAME" "Caret documents (~1 G tokens, ctx/noctx) and clean eval positions"
  done_ upload
fi
log finished
