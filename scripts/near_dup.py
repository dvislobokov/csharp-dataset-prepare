#!/usr/bin/env python3
"""
Near-duplicate decontamination of the held-out splits: which validation/test (and optional extra eval) files are near-copies
of a training file of the corpus. Exact cross-repository duplicates were already removed during extraction (file_owner by
sha256); this catches edited copies (forks, vendored libraries, templates).

Method: code tokens (identifiers, numbers, punctuation runs; whitespace and comments kept out of the token stream only by
the regex), 5-token shingles, MinHash with 128 permutations, LSH banding 16 x 8 over the TRAIN signatures, candidates
verified by the MinHash Jaccard estimate (>= --threshold, default 0.8 like the engine's dedup). Deterministic: token
hashes are blake2b-derived, permutations from a fixed seed.

  python -I scripts/near_dup.py --corpus /srv/flc/engine-shards/src/data/corpus --out /srv/flc/neardup/csharp \\
      [--extra-eval /srv/flc/eval-fresh/corpus] --workers 40
"""
from __future__ import annotations

import argparse
import glob
import hashlib
import json
import multiprocessing as mp
import os
import re
import time

import numpy as np

NPERM, BANDS, ROWS, SHINGLE = 128, 16, 8, 5
TOKEN = re.compile(r"[A-Za-z_][A-Za-z0-9_]*|\d+|[^\sA-Za-z0-9_]")
MERSENNE = np.uint64((1 << 61) - 1)
_rng = np.random.default_rng(20261009)
PERM_A = _rng.integers(1, (1 << 61) - 1, NPERM, dtype=np.uint64)
PERM_B = _rng.integers(0, (1 << 61) - 1, NPERM, dtype=np.uint64)
_tokcache: dict[str, int] = {}


def tok_hash(t: str) -> int:
    h = _tokcache.get(t)
    if h is None:
        h = int.from_bytes(hashlib.blake2b(t.encode("utf-8", "surrogatepass"), digest_size=8).digest(), "little") >> 3
        if len(_tokcache) < 2_000_000:
            _tokcache[t] = h
    return h


def signature(text: str) -> np.ndarray | None:
    toks = TOKEN.findall(text)
    if len(toks) < SHINGLE + 5:
        return None  # too small to say anything; never flagged
    h = np.fromiter((tok_hash(t) for t in toks), dtype=np.uint64, count=len(toks))
    n = len(h) - SHINGLE + 1
    sh = np.zeros(n, dtype=np.uint64)
    for k in range(SHINGLE):  # polynomial combination, wraps mod 2^64
        sh = sh * np.uint64(1099511628211) + h[k:k + n]
    sh = np.unique(sh) % MERSENNE
    # (a*x + b) mod p for every permutation; uint64 products wrap, acceptable for a hash family here
    sig = np.empty(NPERM, dtype=np.uint64)
    for i in range(0, NPERM, 16):
        v = (PERM_A[i:i + 16, None] * sh[None, :] + PERM_B[i:i + 16, None]) % MERSENNE
        sig[i:i + 16] = v.min(axis=1)
    return sig


def _work(unit):
    import pyarrow.parquet as pq
    path, rg = unit
    t = pq.ParquetFile(path).read_row_group(rg, columns=["repository_id", "relative_path", "sha256", "content"])
    rows, sigs = [], []
    for r, p, s, c in zip(*(t.column(i).to_pylist() for i in range(4))):
        sig = signature(c or "")
        if sig is None:
            continue
        rows.append((r, p, s))
        sigs.append(sig)
    return rows, (np.stack(sigs) if sigs else np.zeros((0, NPERM), dtype=np.uint64))


def signatures(files, workers):
    import pyarrow.parquet as pq
    units = [(f, rg) for f in files for rg in range(pq.ParquetFile(f).num_row_groups)]
    rows, sigs = [], []
    with mp.Pool(workers) as pool:
        for r, s in pool.imap(_work, units, chunksize=1):
            rows += r
            sigs.append(s)
    return rows, (np.concatenate(sigs) if sigs else np.zeros((0, NPERM), dtype=np.uint64))


def band_keys(sigs: np.ndarray) -> np.ndarray:
    """[n, BANDS] uint64 keys: blake-free mixing of the ROWS values of each band."""
    b = sigs.reshape(len(sigs), BANDS, ROWS)
    k = np.zeros((len(sigs), BANDS), dtype=np.uint64)
    for j in range(ROWS):
        k = k * np.uint64(0x9E3779B97F4A7C15) + b[:, :, j]
    return k


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--corpus", required=True, help="directory with <split>-*.parquet of the corpus config")
    ap.add_argument("--extra-eval", action="append", default=[], help="more eval corpus dirs (all splits are eval)")
    ap.add_argument("--out", required=True)
    ap.add_argument("--threshold", type=float, default=0.8)
    ap.add_argument("--workers", type=int, default=40)
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    t0 = time.time()
    train_rows, train_sig = signatures(sorted(glob.glob(os.path.join(a.corpus, "train-*.parquet"))), a.workers)
    print(json.dumps({"event": "train_signatures", "files": len(train_rows), "s": round(time.time() - t0)}), flush=True)
    keys = band_keys(train_sig)
    index = []  # per band: sorted keys and the train row ids
    for bnd in range(BANDS):
        order = np.argsort(keys[:, bnd], kind="stable")
        index.append((keys[order, bnd], order))
    del keys
    evals = {"validation": sorted(glob.glob(os.path.join(a.corpus, "validation-*.parquet"))),
             "test": sorted(glob.glob(os.path.join(a.corpus, "test-*.parquet")))}
    for k, d in enumerate(a.extra_eval):
        evals[f"extra{k}:{os.path.basename(os.path.normpath(d))}"] = sorted(glob.glob(os.path.join(d, "*.parquet")))
    summary = {"threshold": a.threshold, "nperm": NPERM, "bands": BANDS, "rows": ROWS, "shingle": SHINGLE,
               "train_files_signed": len(train_rows), "sets": {}}
    out_f = open(os.path.join(a.out, "near_duplicates.jsonl"), "w")
    for name, files in evals.items():
        rows, sig = signatures(files, a.workers)
        ek = band_keys(sig)
        best = np.full(len(rows), -1, dtype=np.int64)
        best_j = np.zeros(len(rows), dtype=np.float32)
        for bnd in range(BANDS):
            sk, ids = index[bnd]
            pos = np.searchsorted(sk, ek[:, bnd])
            for i in np.nonzero((pos < len(sk)) & (sk[np.minimum(pos, len(sk) - 1)] == ek[:, bnd]))[0]:
                p = pos[i]
                while p < len(sk) and sk[p] == ek[i, bnd]:
                    j = ids[p]
                    est = float(np.mean(train_sig[j] == sig[i]))
                    if est > best_j[i]:
                        best_j[i], best[i] = est, j
                    p += 1
                    if p - pos[i] > 64:  # pathological buckets (boilerplate): enough candidates seen
                        break
        flagged = np.nonzero(best_j >= a.threshold)[0]
        per_repo: dict[str, list[int]] = {}
        for i, (r, _, _) in enumerate(rows):
            per_repo.setdefault(r, [0, 0])[0] += 1
        for i in flagged:
            r, p, s = rows[i]
            tr, tp, ts = train_rows[best[i]]
            per_repo[r][1] += 1
            out_f.write(json.dumps({"set": name, "repository_id": r, "relative_path": p, "sha256": s, "jaccard_est": round(float(best_j[i]), 3),
                                    "train_repository_id": tr, "train_relative_path": tp, "train_sha256": ts}) + "\n")
        repos = {r: {"files": n, "near_dup_files": d, "share": round(d / n, 3)} for r, (n, d) in per_repo.items()}
        heavy = sorted((r for r, v in repos.items() if v["share"] >= 0.5 and v["files"] >= 5), key=lambda r: -repos[r]["share"])
        summary["sets"][name] = {"files_signed": len(rows), "near_dup_files": int(len(flagged)),
                                 "near_dup_share": round(len(flagged) / max(len(rows), 1), 4), "repos": len(repos),
                                 "repos_with_near_dups": sum(1 for v in repos.values() if v["near_dup_files"]),
                                 "repos_mostly_duplicated": heavy}
        json.dump(repos, open(os.path.join(a.out, f"repos-{name.replace(':', '_')}.json"), "w"), indent=1)
        print(json.dumps({"event": "set_done", "set": name, **{k: v for k, v in summary["sets"][name].items() if k != "repos_mostly_duplicated"},
                          "repos_mostly_duplicated": len(heavy), "s": round(time.time() - t0)}), flush=True)
    out_f.close()
    summary["seconds"] = round(time.time() - t0)
    json.dump(summary, open(os.path.join(a.out, "summary.json"), "w"), indent=1)


if __name__ == "__main__":
    main()
