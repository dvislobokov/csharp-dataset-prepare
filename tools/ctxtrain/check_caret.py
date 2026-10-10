#!/usr/bin/env python3
"""
Checks of a make_docs.py output before publishing (scripts/finalize_expand.sh step 5):

- train shards ctx/noctx: offsets monotone and matching the token file, loss_start inside each document, ids < vocab size,
  every completion ends with <|endoftext|> and contains no other special token and no CR/LF, ctx and noctx carry the same
  completions in the same order, the noctx prompt has the engine's SPM layout;
- eval positions: non-empty single-line targets, prompts within the context, unique sample ids;
- repository isolation: no repository of the eval-fresh corpus occurs in the training corpus (any split), and none of the
  eval positions comes from a training repository;
- a few decoded examples for reading.

  python -I check_caret.py --engine <idea-ml-completion> --vocab cs-16384.bpe --docs caret/cs-16384/train \\
      --eval caret/cs-16384/eval --corpus <corpus dir> --fresh-corpus <eval-fresh corpus dir> --out CHECKS.json
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import random
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import flcctx as F  # noqa: E402


def repo_ids(d):
    import pyarrow.parquet as pq
    out = set()
    for f in sorted(glob.glob(os.path.join(d, "*.parquet"))):
        out.update(pq.read_table(f, columns=["repository_id"]).column(0).unique().to_pylist())
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--engine", required=True)
    ap.add_argument("--vocab", required=True)
    ap.add_argument("--docs", required=True)
    ap.add_argument("--eval", required=True)
    ap.add_argument("--corpus", required=True)
    ap.add_argument("--fresh-corpus", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--examples", type=int, default=3)
    a = ap.parse_args()
    F.engine_paths(a.engine)
    tok = F.Tok(a.vocab)
    special = {tok.fim_prefix, tok.fim_suffix, tok.fim_middle, tok.file_sep, tok.eot, tok.r0, tok.r1, tok.r2}
    report, errors = {"train": {}, "eval": {}, "isolation": {}, "examples": []}, []

    mids = {}
    for v in ("noctx", "ctx"):
        p = os.path.join(a.docs, v)
        t = np.memmap(p + ".tokens.u16", dtype=np.uint16, mode="r")
        o = np.fromfile(p + ".offsets.u64", dtype=np.uint64)
        ls = np.fromfile(p + ".loss_start.u32", dtype=np.uint32)
        n = len(ls)
        lens = np.diff(o).astype(np.int64)
        ok = (len(o) == n + 1 and o[0] == 0 and int(o[-1]) == len(t) and bool((lens > 0).all())
              and bool((ls.astype(np.int64) < lens).all()) and bool((ls > 0).all()))
        if not ok:
            errors.append(f"{v}: offsets/loss_start inconsistent")
        if int(t.max()) >= 16384:
            errors.append(f"{v}: token id >= 16384")
        bad_eot = bad_nl = bad_special = bad_layout = 0
        mid_list = []
        for i in range(n):
            s, e, l = int(o[i]), int(o[i + 1]), int(ls[i])
            mid = t[s + l:e]
            mid_list.append(mid.tobytes())
            if mid[-1] != tok.eot:
                bad_eot += 1
            body = [int(x) for x in mid[:-1]]
            if any(x in special for x in body):
                bad_special += 1
            elif b"\n" in tok.decode(body) or b"\r" in tok.decode(body):
                bad_nl += 1
            if v == "noctx" and not (t[s] == tok.fim_prefix and t[s + 1] == tok.fim_suffix and tok.fim_middle in t[s:s + l]
                                     and tok.file_sep in t[s:s + l]):
                bad_layout += 1
        mids[v] = mid_list
        stats = {"docs": n, "tokens": int(len(t)), "loss_tokens": int((lens - ls.astype(np.int64)).sum()),
                 "mean_doc_tokens": round(float(lens.mean()), 1), "max_doc_tokens": int(lens.max()),
                 "completion_without_eot": bad_eot, "completion_with_newline": bad_nl, "completion_with_special": bad_special}
        if v == "noctx":
            stats["prompt_layout_errors"] = bad_layout
        for k in ("completion_without_eot", "completion_with_newline", "completion_with_special", "prompt_layout_errors"):
            if stats.get(k):
                errors.append(f"{v}: {k}={stats[k]}")
        report["train"][v] = stats
    same = len(mids["ctx"]) == len(mids["noctx"]) and all(x == y for x, y in zip(mids["ctx"], mids["noctx"]))
    report["train"]["ctx_noctx_same_completions"] = same
    if not same:
        errors.append("ctx and noctx completions differ")
    report["train"]["meta"] = json.load(open(os.path.join(a.docs, "meta.json")))["stats"]

    pos = [json.loads(l) for l in open(os.path.join(a.eval, "positions.jsonl"))]
    bad_t = sum(1 for p in pos if not p["target"].strip() or "\n" in p["target"] or "\r" in p["target"])
    too_long = sum(1 for p in pos if len(p["prompt_full"]) > 2048 - 48)
    dup = len(pos) - len({p["sample_id"] for p in pos})
    report["eval"] = {"positions": len(pos), "repos": len({p["repository_id"] for p in pos}), "bad_targets": bad_t,
                      "prompt_over_context": too_long, "duplicate_ids": dup,
                      "with_facts": sum(p["has_facts"] for p in pos), "with_deps": sum(p["has_deps"] for p in pos),
                      "by_kind": {k: sum(1 for p in pos if p["caret_kind"] == k) for k in sorted({p["caret_kind"] for p in pos})}}
    for k in ("bad_targets", "prompt_over_context", "duplicate_ids"):
        if report["eval"][k]:
            errors.append(f"eval: {k}={report['eval'][k]}")

    train_repos, fresh_repos = repo_ids(a.corpus), repo_ids(a.fresh_corpus)
    overlap = sorted(train_repos & fresh_repos)
    eval_in_train = sorted({p["repository_id"] for p in pos} & train_repos)
    report["isolation"] = {"train_corpus_repos": len(train_repos), "fresh_corpus_repos": len(fresh_repos),
                           "fresh_repos_in_train_corpus": len(overlap), "eval_position_repos_in_train": len(eval_in_train),
                           "examples": overlap[:10]}
    if overlap or eval_in_train:
        errors.append(f"isolation: {len(overlap)} eval-fresh repositories in the training corpus")

    names = {tok.fim_prefix: "<|fim_prefix|>", tok.fim_suffix: "<|fim_suffix|>", tok.fim_middle: "<|fim_middle|>",
             tok.file_sep: "<|file_sep|>", tok.eot: "<|endoftext|>", tok.r0: "<|reserved_0|>", tok.r1: "<|reserved_1|>",
             tok.r2: "<|reserved_2|>"}

    def dec(ids):
        out, buf = [], []
        for x in ids:
            if x in names:
                if buf:
                    out.append(tok.decode(buf).decode("utf-8", "replace")); buf = []
                out.append(names[x])
            else:
                buf.append(int(x))
        if buf:
            out.append(tok.decode(buf).decode("utf-8", "replace"))
        return "".join(out)

    t = np.memmap(os.path.join(a.docs, "ctx.tokens.u16"), dtype=np.uint16, mode="r")
    o = np.fromfile(os.path.join(a.docs, "ctx.offsets.u64"), dtype=np.uint64)
    ls = np.fromfile(os.path.join(a.docs, "ctx.loss_start.u32"), dtype=np.uint32)
    rnd = random.Random(1)
    for i in rnd.sample(range(len(ls)), min(a.examples, len(ls))):
        ids = [int(x) for x in t[o[i]:o[i + 1]]]
        p = dec(ids[:ls[i]])
        report["examples"].append({"doc": i, "prompt_tail": p[-600:], "completion": dec(ids[ls[i]:])})

    report["errors"] = errors
    report["ok"] = not errors
    json.dump(report, open(a.out, "w"), indent=1, ensure_ascii=False)
    print(json.dumps({k: report[k] for k in ("train", "eval", "isolation", "errors", "ok")}, ensure_ascii=False)[:4000], flush=True)
    sys.exit(0 if not errors else 1)


if __name__ == "__main__":
    main()
