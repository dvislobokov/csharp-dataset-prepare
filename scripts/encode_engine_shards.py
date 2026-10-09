#!/usr/bin/env python3
"""
Encode the `corpus` config with the plugin engine's tokenizer (idea-ml-completion `cs-16384.bpe`, pre-tokenizer go-code-1)
into the engine's training shard format, so that its `tools/nn/train/train.py` reads them unchanged:

  <fold>.tokens.u16   uint16 ids of all files, concatenated (no separators; the loader adds <|file_sep|> path / <|endoftext|>)
  <fold>.offsets.u64  N+1 offsets;  <fold>.repo.u32  repo id per file;  <fold>.repos.txt;  <fold>.files.txt "repo\\tpath"
  <fold>.meta.json    vocab name/sha256, counts

Folds: lm = our train split, validation, test (repository-grouped splits of the dataset). File bytes = UTF-8 of `content`
(the BOM is not included: the editor document the plugin sees has none). Files are ordered by shard name, then row.

  python -I scripts/encode_engine_shards.py --engine /srv/flc/engine --vocab /srv/flc/engine/cs-16384.bpe \\
      --work /srv/flc/engine-shards --upload
"""
from __future__ import annotations

import argparse
import glob
import hashlib
import json
import multiprocessing as mp
import os
import sys
import time
from array import array

FOLDS = {"lm": "train", "validation": "validation", "test": "test"}
_enc = None


def log(**kw):
    print(json.dumps({"ts": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), **kw}), flush=True)


def _init(engine, vocab):
    global _enc
    sys.path.insert(0, engine)
    import cmlbpe
    _enc = cmlbpe.Encoder(cmlbpe.Vocab.load(vocab))


def _work(path):
    """One parquet shard -> (repo ids, paths, lengths, token bytes, source bytes); round trip checked on every file."""
    import pyarrow.parquet as pq
    t = pq.read_table(path, columns=["repository_id", "relative_path", "content"])
    repos, paths, lens, arr, nbytes = [], [], [], array("H"), 0
    for r, p, c in zip(*(t.column(i).to_pylist() for i in range(3))):
        data = c.encode("utf-8")
        ids = _enc.encode_bytes(data)
        if _enc.decode_bytes(ids) != data:
            raise RuntimeError(f"round trip failed: {r} {p}")
        repos.append(r); paths.append(p); lens.append(len(ids)); arr.extend(ids); nbytes += len(data)
    return repos, paths, lens, arr.tobytes(), nbytes


def encode_fold(files, out_pre, args):
    ftok = open(out_pre + ".tokens.u16.tmp", "wb")
    fidx = open(out_pre + ".files.txt.tmp", "w", encoding="utf-8", newline="\n")
    offsets, repo_ids, repos = [0], [], {}
    ntok = nbytes = 0
    t0 = time.time()
    with mp.Pool(args.workers, _init, (args.engine, args.vocab)) as pool:
        for k, (rs, ps, ls, raw, nb) in enumerate(pool.imap(_work, files)):   # imap keeps shard order
            ftok.write(raw)
            for r, p, n in zip(rs, ps, ls):
                offsets.append(offsets[-1] + n)
                repo_ids.append(repos.setdefault(r, len(repos)))
                fidx.write(f"{r}\t{p}\n")
            ntok += sum(ls); nbytes += nb
            if k % 10 == 0:
                log(event="progress", fold=os.path.basename(out_pre), shards=f"{k + 1}/{len(files)}", tokens=ntok,
                    mb_per_s=round(nbytes / 1e6 / max(time.time() - t0, 1e-9), 1))
    ftok.close(); fidx.close()
    with open(out_pre + ".offsets.u64.tmp", "wb") as f:
        array("Q", offsets).tofile(f)
    with open(out_pre + ".repo.u32.tmp", "wb") as f:
        array("I", repo_ids).tofile(f)
    with open(out_pre + ".repos.txt.tmp", "w", encoding="utf-8", newline="\n") as f:
        f.writelines(r + "\n" for r in repos)
    meta = {"vocab": os.path.basename(args.vocab), "vocab_sha256": hashlib.sha256(open(args.vocab, "rb").read()).hexdigest(),
            "lang": "csharp", "fold": os.path.basename(out_pre), "files": len(repo_ids), "repos": len(repos), "tokens": ntok,
            "bytes": nbytes, "source": f"dvislobokov/csharp-ml-complation corpus/{FOLDS[os.path.basename(out_pre)]}",
            "bom_stripped": True}
    with open(out_pre + ".meta.json.tmp", "w") as f:
        json.dump(meta, f, indent=1)
    for ext in ("tokens.u16", "files.txt", "offsets.u64", "repo.u32", "repos.txt", "meta.json"):
        os.replace(f"{out_pre}.{ext}.tmp", f"{out_pre}.{ext}")
    log(event="fold_done", seconds=round(time.time() - t0), **meta)
    return meta


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--repo", default="dvislobokov/csharp-ml-complation")
    ap.add_argument("--token-file", default="/srv/flc/secrets/HF_TOKEN")
    ap.add_argument("--engine", required=True, help="directory with the engine's cmlbpe.py")
    ap.add_argument("--vocab", required=True)
    ap.add_argument("--work", required=True)
    ap.add_argument("--workers", type=int, default=48)
    ap.add_argument("--upload", action="store_true")
    args = ap.parse_args()
    from huggingface_hub import HfApi, snapshot_download
    token = open(args.token_file).read().strip()
    src = os.path.join(args.work, "src")
    snapshot_download(args.repo, repo_type="dataset", token=token, local_dir=src, allow_patterns=["data/corpus/*.parquet"])
    name = os.path.splitext(os.path.basename(args.vocab))[0]
    out = os.path.join(args.work, name)
    os.makedirs(out, exist_ok=True)
    metas = {}
    for fold, split in FOLDS.items():
        files = sorted(glob.glob(os.path.join(src, "data/corpus", f"{split}-*.parquet")))
        metas[fold] = encode_fold(files, os.path.join(out, fold), args)
    import shutil
    shutil.copy(args.vocab, os.path.join(out, os.path.basename(args.vocab)))
    with open(os.path.join(out, "README.md"), "w") as f:
        f.write(f"""# Engine training shards ({name})

The `corpus` config of this dataset encoded with the plugin engine's tokenizer `{os.path.basename(args.vocab)}`
(https://github.com/dvislobokov/idea-ml-completion, pre-tokenizer go-code-1), in the format of its
`tools/tokenizer/encode_corpus.py`; `tools/nn/train/train.py --data <this folder> --vocab <this folder>/{os.path.basename(args.vocab)}` reads it unchanged.
Folds: `lm` = train split, `validation`, `test` (repository-grouped). BOM not included.

| fold | files | repos | tokens |
|---|---|---|---|
""" + "".join(f"| {k} | {m['files']:,} | {m['repos']:,} | {m['tokens']:,} |\n" for k, m in metas.items()))
    if args.upload:
        t = time.time()
        HfApi(token=token).upload_folder(folder_path=out, path_in_repo=f"engine/{name}", repo_id=args.repo, repo_type="dataset",
                                         commit_message=f"Engine training shards ({name})")
        log(event="uploaded", seconds=round(time.time() - t))
    log(event="done")


if __name__ == "__main__":
    main()
