#!/usr/bin/env python3
"""
Train the final byte-level BPE tokenizers on the whole TRAIN split of the `corpus` config and pre-tokenize every corpus split
into parquet shards for causal pretraining; optionally upload them to the dataset repository under tokenized/<name>/.

One output row per source file (provenance columns kept, text dropped):
  input_ids = <|cs|> <|path|> enc(relative_path + "\\n") <|code|> enc("\\n" + content) <|endoftext|>
i.e. the same frame as flc-prompt/v2 so pretraining and completion share the header. content_start = index of the first id of
enc("\\n" + content); decode(input_ids[content_start:-1]) == "\\n" + content (checked on a sample of every shard).
Files containing a literal special-token string are skipped and counted (the tokenizer would match them as specials).

  python -I scripts/tokenize_corpus.py --work /srv/flc/tokenized --vocabs 16000,24000 --upload
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tokenizer_bench import SPECIAL, train_bpe  # noqa: E402 - same training settings as the study

SPLITS = ("train", "validation", "test")
META = ["repository_id", "revision", "license", "relative_path", "sha256", "project", "is_test"]
README_MARK = "<!-- tokenized-corpus -->"


def log(**kw):
    print(json.dumps({"ts": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), **kw}), flush=True)


def download(repo, token, dest):
    from huggingface_hub import snapshot_download
    snapshot_download(repo, repo_type="dataset", token=token, local_dir=dest, allow_patterns=["data/corpus/*.parquet"])
    files = {sp: sorted(os.path.join(dest, "data/corpus", f) for f in os.listdir(os.path.join(dest, "data/corpus"))
                        if f.startswith(sp + "-") and f.endswith(".parquet")) for sp in SPLITS}
    return files


def iter_content(files):
    import pyarrow.parquet as pq
    for f in files:
        for batch in pq.ParquetFile(f).iter_batches(columns=["content"], batch_size=4096):
            yield from (t for t in batch.column(0).to_pylist() if t)


def tokenize_shard(tok, src, dst, check_every=50):
    import numpy as np
    import pyarrow as pa
    import pyarrow.parquet as pq
    t = pq.read_table(src, columns=META + ["content"])
    content = t.column("content").to_pylist()
    paths = t.column("relative_path").to_pylist()
    keep = [i for i, c in enumerate(content) if c and not any(s in c for s in SPECIAL)]
    skipped = len(content) - len(keep)
    sp = {s: tok.token_to_id(s) for s in ("<|cs|>", "<|path|>", "<|code|>", "<|endoftext|>")}
    heads = tok.encode_batch([paths[i] + "\n" for i in keep], add_special_tokens=False)
    bodies = tok.encode_batch(["\n" + content[i] for i in keep], add_special_tokens=False)
    lengths, starts, chunks = [], [], []
    for k, (h, b) in enumerate(zip(heads, bodies)):
        ids = [sp["<|cs|>"], sp["<|path|>"], *h.ids, sp["<|code|>"]]
        starts.append(len(ids))
        ids += b.ids
        ids.append(sp["<|endoftext|>"])
        lengths.append(len(ids))
        chunks.append(np.asarray(ids, dtype=np.uint16))
        if k % check_every == 0 and tok.decode(b.ids, skip_special_tokens=False) != "\n" + content[keep[k]]:
            raise RuntimeError(f"round trip failed: {src} row {keep[k]}")
    offsets = np.zeros(len(chunks) + 1, dtype=np.int64)
    np.cumsum(lengths, out=offsets[1:])
    values = np.concatenate(chunks) if chunks else np.zeros(0, dtype=np.uint16)
    out = t.select(META).take(pa.array(keep, type=pa.int64()))
    out = out.append_column("n_tokens", pa.array(lengths, type=pa.int32()))
    out = out.append_column("content_start", pa.array(starts, type=pa.int32()))
    out = out.append_column("input_ids", pa.LargeListArray.from_arrays(pa.array(offsets), pa.array(values)))
    tmp = dst + ".tmp"
    pq.write_table(out, tmp, compression="zstd", row_group_size=2048)
    os.replace(tmp, dst)
    return {"files": len(keep), "skipped_special_literal": skipped, "tokens": int(offsets[-1]),
            "content_chars": sum(len(content[i]) for i in keep)}


def readme_section(names, stats):
    rows = "".join(
        f"| `corpus_tokens_{n.split('-')[-1]}` | `tokenized/{n}/` | {stats[n]['vocab']:,} | "
        + " / ".join(f"{stats[n]['splits'][sp]['tokens'] / 1e6:,.0f}M" for sp in SPLITS) + " |\n" for n in names)
    return f"""{README_MARK}
## Tokenized corpus (pretraining)

The `corpus` config pre-tokenized with the project's byte-level BPE tokenizers (trained on the `corpus` **train** split only;
`tokenizer.json` sits next to the shards). One row per source file with provenance columns, `n_tokens`, `content_start` and
`input_ids` (uint16). Every document is framed like the completion prompt:
`<|cs|><|path|>` path `\\n` `<|code|>` `\\n` + content `<|endoftext|>`.

| Config | Folder | Vocab | Tokens train / validation / test |
|---|---|---|---|
{rows}
```python
import numpy as np
from datasets import load_dataset
ds = load_dataset("dvislobokov/csharp-ml-complation", "corpus_tokens_16k", split="train")
tokens = np.concatenate([np.asarray(x, dtype=np.uint16) for x in ds["input_ids"]])   # packed stream for np.memmap / nanoGPT
tokens.tofile("train.bin")
```
{README_MARK}
"""


def patch_readme(api, repo, names, stats):
    from huggingface_hub import hf_hub_download
    text = open(hf_hub_download(repo, "README.md", repo_type="dataset", token=api.token, force_download=True)).read()
    head, sep, body = text.partition("\n---\n")
    for n in names:
        cfg = f"corpus_tokens_{n.split('-')[-1]}"
        if f"config_name: {cfg}\n" in head:
            continue
        head += f"\n- config_name: {cfg}\n  data_files:" + "".join(
            f"\n  - split: {sp}\n    path: tokenized/{n}/corpus/{sp}-*.parquet" for sp in SPLITS)
    if README_MARK in body:
        a = body.index(README_MARK)
        b = body.index(README_MARK, a + 1) + len(README_MARK) + 1
        body = body[:a] + body[b:]
    marker = "## Progress"
    sec = readme_section(names, stats)
    body = body.replace(marker, sec + "\n" + marker, 1) if marker in body else body + "\n" + sec
    api.upload_file(path_or_fileobj=(head + sep + body).encode(), path_in_repo="README.md", repo_id=repo, repo_type="dataset",
                    commit_message="README: tokenized corpus configs")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--repo", default="dvislobokov/csharp-ml-complation")
    ap.add_argument("--token-file", default="/srv/flc/secrets/HF_TOKEN")
    ap.add_argument("--work", required=True)
    ap.add_argument("--vocabs", default="16000,24000")
    ap.add_argument("--threads", type=int, default=96)
    ap.add_argument("--upload", action="store_true")
    args = ap.parse_args()
    os.environ["RAYON_NUM_THREADS"] = str(args.threads)
    from huggingface_hub import HfApi
    token = open(args.token_file).read().strip()
    api = HfApi(token=token)
    t0 = time.time()
    files = download(args.repo, token, os.path.join(args.work, "src"))
    log(event="downloaded", shards={sp: len(v) for sp, v in files.items()}, seconds=round(time.time() - t0, 1))

    names, stats = [], {}
    for v in [int(x) for x in args.vocabs.split(",")]:
        name = f"csharp-bpe-{v // 1000}k"
        root = os.path.join(args.work, "tokenized", name)
        os.makedirs(os.path.join(root, "corpus"), exist_ok=True)
        t = time.time()
        tpath = os.path.join(root, "tokenizer.json")
        if os.path.exists(tpath):  # resume: reuse the tokenizer the shards must match
            from tokenizers import Tokenizer
            tok = Tokenizer.from_file(tpath)
        else:
            tok = train_bpe(iter_content(files["train"]), v, args.threads)
            tok.save(tpath + ".tmp")
            os.replace(tpath + ".tmp", tpath)
        st = {"tokenizer": name, "vocab": tok.get_vocab_size(), "trained_on": "corpus/train (all shards)",
              "tokenizer_sha256": hashlib.sha256(open(tpath, "rb").read()).hexdigest(), "train_s": round(time.time() - t, 1),
              "frame": "<|cs|><|path|>{path}\\n<|code|>\\n{content}<|endoftext|>", "splits": {}}
        log(event="trained", tokenizer=name, seconds=st["train_s"])
        t = time.time()
        for sp in SPLITS:
            acc = {"shards": 0, "files": 0, "skipped_special_literal": 0, "tokens": 0, "content_chars": 0}
            for f in files[sp]:
                r = tokenize_shard(tok, f, os.path.join(root, "corpus", os.path.basename(f)))
                acc["shards"] += 1
                for k in r:
                    acc[k] += r[k]
            acc["chars_per_token"] = round(acc["content_chars"] / max(acc["tokens"], 1), 3)
            st["splits"][sp] = acc
            log(event="tokenized", tokenizer=name, split=sp, **acc)
        st["tokenize_s"] = round(time.time() - t, 1)
        json.dump(st, open(os.path.join(root, "stats.json"), "w"), indent=2)
        names.append(name)
        stats[name] = st
        if args.upload:
            t = time.time()
            api.upload_folder(folder_path=root, path_in_repo=f"tokenized/{name}", repo_id=args.repo, repo_type="dataset",
                              commit_message=f"Tokenized corpus ({name})")
            log(event="uploaded", tokenizer=name, seconds=round(time.time() - t, 1))
    if args.upload:
        patch_readme(api, args.repo, names, stats)
        log(event="readme_updated")
    log(event="done", seconds=round(time.time() - t0, 1))


if __name__ == "__main__":
    main()
