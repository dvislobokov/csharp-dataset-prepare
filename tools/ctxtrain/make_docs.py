#!/usr/bin/env python3
"""
Caret documents of flc-context/v1.1 (docs/CONTEXT_SPEC-RU.md section 10) from the C# dataset: samples + semantic
(semantic-fix/ records override) + corpus file text + deps profiles. Output: training shards and/or evaluation positions.

Training shard (per variant): <out>/<variant>.tokens.u16, .offsets.u64 (N+1), .loss_start.u32 (prompt length per doc),
.meta.json. Loss = positions >= loss_start (the typed remainder + target + <|endoftext|>).
Variants: `ctx` (75 % with a facts block, one corruption with p 0.4; profile in 80 %), `noctx` (the same documents,
never a profile or a block — the control). Both are built from the SAME sampled positions in the same order.
Evaluation file (--eval): JSONL, one row per position with the three prompts (none / deps / full), typed, target.

  python -I make_docs.py --engine <idea-ml-completion> --vocab cs-16384.bpe --hf-repo dvislobokov/csharp-ml-complation \\
      --split train --docs 300000 --out /srv/flc/ctxdocs/train --workers 48
  python -I make_docs.py ... --prefix eval-fresh/ --eval --docs 12000 --out /srv/flc/ctxdocs/eval-fresh
"""
from __future__ import annotations

import argparse
import collections
import glob
import hashlib
import json
import multiprocessing as mp
import os
import random
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import flcctx as F  # noqa: E402

_tok = None
_args = None


def log(**kw):
    print(json.dumps({"ts": time.strftime("%H:%M:%S"), **kw}), flush=True)


def u(seed: str, *parts) -> float:
    h = hashlib.sha256("\x1f".join([seed, *map(str, parts)]).encode()).digest()
    return int.from_bytes(h[:8], "little") / 2 ** 64


def editor_text(content: str, sample: dict, has_bom: bool):
    """UTF-8 editor text (LF, no BOM) with the target removed; caret byte offset; target bytes. None on mismatch."""
    raw = content.encode("utf-8")
    shift = 3 if has_bom else 0
    caret, te = sample["caret_byte_offset"] - shift, sample["target_end_byte_offset"] - shift
    target = raw[caret:te]
    if target != sample["target_text"].encode("utf-8"):
        return None
    crlf_before = raw.count(b"\r\n", 0, caret)
    text = (raw[:caret] + raw[te:]).replace(b"\r\n", b"\n")
    return text, caret - crlf_before, target


def corrupt(lines: dict, sem: dict, rnd: random.Random) -> dict:
    """One typed corruption (v1.1 10.2) that mimics plugin failures."""
    lines = {k: list(v) for k, v in lines.items()}
    kind = rnd.randrange(5)
    if kind == 0 and "MEMBER" in lines:
        lines.pop("MEMBER")                                   # receiver known, members not
    elif kind == 1 and "MEMBER" in lines:
        ext = {F.item_of(f) for f in sem.get("members") or [] if f.get("is_extension")}
        lines["MEMBER"] = [x for x in lines["MEMBER"] if x not in ext]
    elif kind == 2:
        lines = {k: [x for x in v if rnd.random() > 0.3] for k, v in lines.items()}
    elif kind == 3:
        lines = {k: v for k, v in lines.items() if k in ("RET", "ARG", "LOCAL")}
    elif lines:
        lines.pop(rnd.choice(sorted(lines)))
    return {k: v for k, v in lines.items() if v}


def _init(engine, vocab, args):
    global _tok, _args
    F.engine_paths(engine)
    F.LANG = args.lang
    _tok = F.Tok(vocab)
    _args = args


def _work(job):
    """One repository: its sampled positions -> documents (training) or evaluation rows."""
    rid, rows, files, sems, deps = job
    out, stats = [], collections.Counter()
    a = _args
    for s in rows:
        f = files.get((s["relative_path"], s["source_sha256"]))
        if f is None:
            stats["no_file"] += 1
            continue
        et = editor_text(f["content"], s, f["has_bom"])
        if et is None:
            stats["text_mismatch"] += 1
            continue
        text, caret, target = et
        sem = sems.get(s["sample_id"])
        line_start = text.rfind(b"\n", 0, caret) + 1
        lines = F.fact_lines(sem, F.prefix_identifiers(text, line_start)) if sem and sem.get("status") in ("resolved", "partially_resolved") else {}
        roots = deps.get(s["relative_path"], [])
        d_ids = F.deps_ids(_tok, roots)
        seed = f"{a.seed}:{s['sample_id']}"
        if a.eval:
            full = F.build(_tok, text, caret, s["relative_path"], d_ids, F.facts_block(_tok, lines) if lines else [])
            dep = F.build(_tok, text, caret, s["relative_path"], d_ids, [])
            none = F.build(_tok, text, caret, s["relative_path"], [], [])
            stats["split_mismatch"] += not full.split_ok
            out.append({"sample_id": s["sample_id"], "repository_id": rid, "path": s["relative_path"], "caret_kind": s["caret_kind"],
                        "target": target.decode("utf-8"), "typed": none.typed.decode("utf-8", "surrogateescape"),
                        "has_facts": full.facts_tokens > 0, "has_deps": full.deps_tokens > 0,
                        "prompt_none": none.ids, "prompt_deps": dep.ids, "prompt_full": full.ids})
            continue
        # training: same position for both variants
        rnd = random.Random(seed)
        with_facts = bool(lines) and u(seed, "facts") < 0.75
        with_deps = bool(d_ids) and u(seed, "deps") < 0.8
        fl = corrupt(lines, sem, rnd) if with_facts and u(seed, "corrupt") < 0.4 else lines
        p_ctx = F.build(_tok, text, caret, s["relative_path"], d_ids if with_deps else [], F.facts_block(_tok, fl) if with_facts and fl else [])
        p_no = F.build(_tok, text, caret, s["relative_path"], [], [])
        mid = _tok.encode(text[p_no.boundary:caret] + target) + [_tok.eot]
        if len(mid) > 256 or len(p_ctx.ids) + len(mid) > 2048:
            stats["too_long"] += 1
            continue
        stats["split_mismatch"] += not p_ctx.split_ok
        stats["with_facts"] += p_ctx.facts_tokens > 0
        stats["with_deps"] += p_ctx.deps_tokens > 0
        out.append((p_ctx.ids, p_no.ids, mid))
    stats["docs"] += len(out)
    return out, stats


def load_inputs(a):
    """Sampled positions grouped by repository, with their file texts, semantic rows (fix overrides) and deps."""
    import pyarrow.parquet as pq
    from huggingface_hub import HfApi, hf_hub_download
    token = open(a.token_file).read().strip()
    api = HfApi(token=token)
    files = api.list_repo_files(a.hf_repo, repo_type="dataset")
    pre = a.prefix
    shard_names = sorted(f for f in files if f.startswith(pre + "data/samples/") and f.endswith(".parquet")
                         and (a.split == "all" or os.path.basename(f).startswith(a.split + "-")))
    rnd = random.Random(a.seed)
    rnd.shuffle(shard_names)
    cache = os.path.join(a.out, "hf")
    dl = lambda n: hf_hub_download(a.hf_repo, n, repo_type="dataset", token=token, cache_dir=cache)
    picked, per_repo_cap, scanned = [], a.per_repo, []
    excluded = set()
    if a.exclude:
        for line in open(a.exclude):
            r = json.loads(line)
            excluded.add((r["repository_id"], r["relative_path"]))
    cols = ["sample_id", "repository_id", "relative_path", "source_sha256", "caret_byte_offset", "target_end_byte_offset",
            "target_text", "caret_kind"]
    for n in shard_names:
        t = pq.read_table(dl(n), columns=cols).to_pylist()
        scanned.append(n)
        by = collections.defaultdict(list)
        for r in t:
            if (r["repository_id"], r["relative_path"]) in excluded:
                continue
            if r["caret_kind"] in a.kinds or not a.kinds:
                by[r["repository_id"]].append(r)
        for rid, rows in by.items():
            rows.sort(key=lambda r: u(str(a.seed), r["sample_id"]))
            picked += rows[:per_repo_cap]
        log(event="samples_shard", name=n, picked=len(picked))
        if len(picked) >= a.docs * 1.15:
            break
    picked.sort(key=lambda r: u(str(a.seed), "order", r["sample_id"]))
    picked = picked[: int(a.docs * 1.15)]                   # some are dropped later (too long, mismatch)
    need_ids = {r["sample_id"] for r in picked}
    need_repos = {r["repository_id"] for r in picked}
    # semantic: data/semantic of the same shards + semantic-fix overrides
    sems = {}
    for n in scanned:
        m = n.replace("/samples/", "/semantic/")
        if m not in files:
            continue
        for r in pq.read_table(dl(m)).to_pylist():
            if r["sample_id"] in need_ids and r["visibility_policy"] == "editor_snapshot":
                sems[r["sample_id"]] = r
    fixes = 0
    for m in sorted(f for f in files if f.startswith(pre + "semantic-fix/") and f.endswith(".parquet")):
        for r in pq.read_table(dl(m)).to_pylist():
            if r["sample_id"] in need_ids:
                sems[r["sample_id"]] = r
                fixes += 1
    log(event="semantic", records=len(sems), fixed=fixes)
    # file texts
    need_files = {(r["repository_id"], r["relative_path"], r["source_sha256"]) for r in picked}
    texts = collections.defaultdict(dict)
    corpus = (sorted(glob.glob(os.path.join(a.corpus_dir, "*.parquet"))) if a.corpus_dir else
              [dl(c) for c in sorted(f for f in files if f.startswith(pre + "data/corpus/") and f.endswith(".parquet"))])
    for c in corpus:
        tbl = pq.read_table(c, columns=["repository_id", "relative_path", "sha256", "content", "has_bom"])
        for r in tbl.to_pylist():
            if (r["repository_id"], r["relative_path"], r["sha256"]) in need_files:
                texts[r["repository_id"]][(r["relative_path"], r["sha256"])] = r
    log(event="files", repos=len(texts), files=sum(len(v) for v in texts.values()))
    # deps profiles (training data: deps/ of the dataset; eval-fresh: computed on the fly is not available -> none)
    deps = collections.defaultdict(dict)
    if a.deps_dir:
        import deps_profile as D
        rr = pq.read_table(os.path.join(a.deps_dir, "repo_roots.parquet")).to_pylist()
        counts = collections.defaultdict(dict)
        for r in rr:
            if r["repository_id"] in need_repos:
                counts[r["repository_id"]][r["root"]] = r["files"]
        for r in pq.read_table(os.path.join(a.deps_dir, "file_roots.parquet")).to_pylist():
            if r["repository_id"] in need_repos:
                deps[r["repository_id"]][r["relative_path"]] = D.profile_for(counts[r["repository_id"]], r["roots"])
    by_repo = collections.defaultdict(list)
    for r in picked:
        by_repo[r["repository_id"]].append(r)
    return [(rid, rows, texts.get(rid, {}), {r["sample_id"]: sems[r["sample_id"]] for r in rows if r["sample_id"] in sems},
             deps.get(rid, {})) for rid, rows in sorted(by_repo.items())]


def write_shard(prefix, docs):
    import numpy as np
    toks = np.concatenate([np.asarray(d[0] + d[1], dtype=np.uint16) for d in docs]) if docs else np.zeros(0, np.uint16)
    offs = np.zeros(len(docs) + 1, dtype=np.uint64)
    np.cumsum([len(d[0]) + len(d[1]) for d in docs], out=offs[1:])
    loss = np.asarray([len(d[0]) for d in docs], dtype=np.uint32)
    toks.tofile(prefix + ".tokens.u16")
    offs.tofile(prefix + ".offsets.u64")
    loss.tofile(prefix + ".loss_start.u32")
    return int(offs[-1])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--engine", required=True, help="idea-ml-completion checkout (for cmlbpe)")
    ap.add_argument("--vocab", required=True)
    ap.add_argument("--lang", choices=["csharp", "go"], default="csharp")
    ap.add_argument("--hf-repo", default="dvislobokov/csharp-ml-complation")
    ap.add_argument("--prefix", default="", help="dataset folder prefix, e.g. eval-fresh/")
    ap.add_argument("--split", default="train")
    ap.add_argument("--docs", type=int, default=300000)
    ap.add_argument("--kinds", nargs="*", default=[])
    ap.add_argument("--per-repo", type=int, default=40, help="max sampled positions per repository")
    ap.add_argument("--exclude", help="JSONL with repository_id/relative_path to skip (decontam near_duplicates)")
    ap.add_argument("--corpus-dir", help="local dir with the corpus parquet files (instead of downloading them)")
    ap.add_argument("--deps-dir", help="local dir with deps file_roots.parquet / repo_roots.parquet")
    ap.add_argument("--eval", action="store_true")
    ap.add_argument("--out", required=True)
    ap.add_argument("--workers", type=int, default=32)
    ap.add_argument("--seed", type=int, default=20261009)
    ap.add_argument("--token-file", default="/srv/flc/secrets/HF_TOKEN")
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "scripts"))
    jobs = load_inputs(a)
    log(event="inputs", repos=len(jobs), positions=sum(len(j[1]) for j in jobs))
    stats, results = collections.Counter(), []
    with mp.Pool(a.workers, _init, (a.engine, a.vocab, a)) as pool:
        for out, st in pool.imap(_work, jobs, chunksize=1):      # imap: deterministic order
            results += out
            stats.update(st)
    if a.eval:
        with open(os.path.join(a.out, "positions.jsonl"), "w") as f:
            for r in results[: a.docs]:
                f.write(json.dumps(r) + "\n")
    else:
        docs = results[: a.docs]
        n_ctx = write_shard(os.path.join(a.out, "ctx"), [(d[0], d[2]) for d in docs])
        n_no = write_shard(os.path.join(a.out, "noctx"), [(d[1], d[2]) for d in docs])
        stats.update({"tokens_ctx": n_ctx, "tokens_noctx": n_no})
    json.dump({"args": {k: v for k, v in vars(a).items() if k != "token_file"}, "stats": dict(stats)},
              open(os.path.join(a.out, "meta.json"), "w"), indent=1)
    log(event="done", **dict(stats))


if __name__ == "__main__":
    main()
