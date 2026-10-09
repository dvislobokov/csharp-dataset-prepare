#!/usr/bin/env python3
"""
Recompute C# semantic records for the samples affected by two extractor fixes (commit "Semantics: no rank leak …"):
  * caret inside a partly typed member name after '.', '?.' or in a qualified name (`obj.Na|`): RECV/MEMBER were missing;
  * records with capped lists (`truncated`): the old "already used in the prefix" ranking could keep the hidden answer.

  select : read data/samples + data/semantic shard pairs of the HF dataset, write per-repository sample JSONL
  run    : per repository fetch the exact recorded revision (sparse, shallow, by SHA), run `flc-dataset semantic-redo`
           with the bulk config (same discovery as the original extraction), keep only the output JSONL
  pack   : parquet with the `semantic` schema of the dataset, uploaded under semantic-fix/<split>-<n>.parquet; a record
           there replaces the record with the same sample_id in data/semantic.

Resumable: every stage skips repositories/shards that are already done. Never builds or runs repository code.

  python -I scripts/semantic_redo.py select --work /srv/flc/redo
  python -I scripts/semantic_redo.py run    --work /srv/flc/redo --jobs 48
  python -I scripts/semantic_redo.py pack   --work /srv/flc/redo --upload
"""
from __future__ import annotations

import argparse
import concurrent.futures as cf
import gzip
import json
import os
import re
import shutil
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import flc_run as fr  # noqa: E402 - shared helpers: git_env, run, schemas, pick, slug, SPARSE

REPO = "dvislobokov/csharp-ml-complation"
IDENT_AT_END = re.compile(r"[A-Za-z_][A-Za-z0-9_]*$")


def log(**kw):
    print(json.dumps({"ts": time.strftime("%H:%M:%S"), **kw}, ensure_ascii=False), flush=True)


def after_dot_partial(left: str, kind: str) -> bool:
    if kind != "identifier_partial":
        return False
    m = IDENT_AT_END.search(left)
    return bool(m) and m.start() > 0 and left[m.start() - 1] == "."


# ------------------------------------------------------------------------------------------------------------- select
def _select_shard(args):
    name, work, token = args
    import pyarrow.parquet as pq
    from huggingface_hub import hf_hub_download
    split = os.path.basename(name).split("-")[0]
    cache = os.path.join(work, "hf")
    from huggingface_hub.errors import EntryNotFoundError
    sp = hf_hub_download(REPO, name, repo_type="dataset", token=token, cache_dir=cache)
    try:  # a batch whose repositories produced no semantic records has no semantic shard
        mp = hf_hub_download(REPO, name.replace("data/samples/", "data/semantic/"), repo_type="dataset", token=token, cache_dir=cache)
        trunc = {r["sample_id"] for r in pq.read_table(mp, columns=["sample_id", "truncated"]).to_pylist() if r["truncated"]}
    except EntryNotFoundError:
        mp, trunc = None, set()
    rows = pq.read_table(sp).to_pylist()
    picked: dict[str, list] = {}
    reasons = {"after_dot_partial": 0, "truncated": 0}
    for r in rows:
        a, t = after_dot_partial(r["left_context"], r["caret_kind"]), r["sample_id"] in trunc
        if not (a or t):
            continue
        reasons["after_dot_partial"] += a
        reasons["truncated"] += t
        r.update({"split": split, "config_sha256": "semantic-redo", "schema_version": "flc-sample/v1"})
        r.pop("license", None)
        picked.setdefault(r["repository_id"], []).append(r)
    for path in (p for p in (sp, mp) if p):
        try:
            os.remove(os.path.realpath(path))  # shards are large; keep the disk free
        except OSError:
            pass
    return name, len(rows), picked, reasons, {r["repository_id"]: (r["revision"], r.get("license")) for r in rows}


def select(a, token):
    from huggingface_hub import HfApi
    os.makedirs(os.path.join(a.work, "inputs"), exist_ok=True)
    done_path = os.path.join(a.work, "select.done")
    done = set(open(done_path).read().split()) if os.path.exists(done_path) else set()
    shards = sorted(f for f in HfApi(token=token).list_repo_files(REPO, repo_type="dataset")
                    if f.startswith("data/samples/") and f.endswith(".parquet") and f not in done)
    meta_path = os.path.join(a.work, "repos.jsonl")
    with cf.ProcessPoolExecutor(a.jobs) as pool, open(done_path, "a") as dl, open(meta_path, "a") as ml:
        for name, n, picked, reasons, meta in pool.map(_select_shard, [(s, a.work, token) for s in shards]):
            for rid, rows in picked.items():
                with gzip.open(os.path.join(a.work, "inputs", fr.slug(rid) + ".jsonl.gz"), "at") as f:
                    for r in rows:
                        f.write(json.dumps(r, ensure_ascii=False) + "\n")
                rev, lic = meta[rid]
                ml.write(json.dumps({"repository_id": rid, "revision": rev, "license": lic,
                                     "split": rows[0]["split"], "group": rows[0]["split_group"]}) + "\n")
            dl.write(name + "\n")
            dl.flush()
            ml.flush()
            log(event="shard", name=name, rows=n, picked=sum(len(v) for v in picked.values()), **reasons)


# ---------------------------------------------------------------------------------------------------------------- run
def run_repo(meta: dict, a, gh_token) -> dict:
    rid = meta["repository_id"]
    s = fr.slug(rid)
    out = os.path.join(a.work, "out", s + ".jsonl")
    if os.path.exists(out):
        return {"repo": rid, "status": "done_before"}
    jd = os.path.join(a.work, "jobs", s)
    shutil.rmtree(jd, ignore_errors=True)
    os.makedirs(jd)
    lp = os.path.join(jd, "job.log")
    src = os.path.join(jd, "src")
    env = fr.git_env(gh_token)
    url = "https://" + rid
    steps = [["git", "init", "-q", src],
             ["git", "-C", src, "remote", "add", "origin", url],
             ["git", "-C", src, "sparse-checkout", "set", "--no-cone", *fr.SPARSE],
             ["git", "-C", src, "fetch", "-q", "--depth", "1", "--filter=blob:none", "origin", meta["revision"]],
             ["git", "-C", src, "checkout", "-q", "FETCH_HEAD"]]
    t0 = time.time()
    try:
        for cmd in steps:
            code, why = fr.run(cmd, env=env, timeout=900, rss_limit=0, log_path=lp)
            if code != 0:
                return {"repo": rid, "status": "failed", "reason": f"git:{cmd[3] if len(cmd) > 3 else cmd[1]}:{why or code}"}
        base = json.load(open(a.base_config))
        base["repository_id"] = rid
        base["license"]["declared"] = meta.get("license")
        base["split"]["repository_split"] = {"validation": "eval"}.get(meta["split"], meta["split"])
        base["split"]["repository_group"] = meta["group"]
        cfg = os.path.join(jd, "config.json")
        json.dump(base, open(cfg, "w"))
        cenv = dict(os.environ, DOTNET_ROOT=a.dotnet_root, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1", DOTNET_gcServer="0")
        tmp = out + ".part"
        code, why = fr.run([a.cli, "semantic-redo", "--repo", src, "--config", cfg, "--samples",
                            os.path.join(a.work, "inputs", s + ".jsonl.gz"), "--out", tmp, "--workers", str(a.workers)],
                           env=cenv, timeout=a.timeout, rss_limit=a.rss_limit_gb * 1024 ** 3, log_path=lp)
        if code != 0:
            return {"repo": rid, "status": "failed", "reason": f"redo:{why or code}"}
        os.replace(tmp, out)
        return {"repo": rid, "status": "ok", "seconds": round(time.time() - t0, 1)}
    finally:
        if not a.keep_work:
            shutil.rmtree(src, ignore_errors=True)


def run_all(a, gh_token):
    os.makedirs(os.path.join(a.work, "out"), exist_ok=True)
    metas = {}
    for line in open(os.path.join(a.work, "repos.jsonl")):
        m = json.loads(line)
        metas[m["repository_id"]] = m
    todo = sorted(metas.values(), key=lambda m: os.path.getsize(os.path.join(a.work, "inputs", fr.slug(m["repository_id"]) + ".jsonl.gz")))
    if a.limit:
        todo = todo[: a.limit]
    log(event="run_start", repos=len(todo))
    res_path = os.path.join(a.work, "run.jsonl")
    with cf.ThreadPoolExecutor(a.jobs) as pool, open(res_path, "a") as rl:
        for r in pool.map(lambda m: run_repo(m, a, gh_token), todo):
            rl.write(json.dumps(r) + "\n")
            rl.flush()
            if r["status"] != "done_before":
                log(event="repo", **r)


# --------------------------------------------------------------------------------------------------------------- pack
def pack(a, token):
    import pyarrow as pa
    import pyarrow.parquet as pq
    sc = fr.schemas()["semantic"]
    metas = {json.loads(l)["repository_id"]: json.loads(l) for l in open(os.path.join(a.work, "repos.jsonl"))}
    by_split: dict[str, list] = {}
    for rid, m in sorted(metas.items()):
        p = os.path.join(a.work, "out", fr.slug(rid) + ".jsonl")
        if not os.path.exists(p):
            continue
        for line in open(p):
            r = json.loads(line)
            if r.get("visibility_policy") != "editor_snapshot":
                continue
            r["repository_id"] = rid
            lk = r.get("leakage") or {}
            r["target_identifiers"] = lk.get("target_identifiers")
            r["covered_target_identifiers"] = lk.get("covered_target_identifiers")
            by_split.setdefault(m["split"], []).append(fr.pick(r, sc))
    dest = os.path.join(a.work, "upload", "semantic-fix")
    shutil.rmtree(dest, ignore_errors=True)
    os.makedirs(dest)
    counts = {}
    for split, rows in by_split.items():
        for k in range(0, len(rows), 500_000):
            pq.write_table(pa.Table.from_pylist(rows[k:k + 500_000], schema=sc),
                           os.path.join(dest, f"{split}-{k // 500_000:04d}.parquet"), compression="zstd")
        counts[split] = len(rows)
    json.dump({"records": counts, "reason": "extractor fixes: partly typed member name after '.', capped-list ranking"},
              open(os.path.join(dest, "summary.json"), "w"), indent=1)
    log(event="packed", **counts)
    if a.upload:
        from huggingface_hub import HfApi
        HfApi(token=token).upload_folder(folder_path=dest, path_in_repo="semantic-fix", repo_id=REPO, repo_type="dataset",
                                         commit_message="semantic-fix: C# records recomputed after extractor fixes")
        log(event="uploaded")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("stage", choices=["select", "run", "pack"])
    ap.add_argument("--work", required=True)
    ap.add_argument("--jobs", type=int, default=16)
    ap.add_argument("--workers", type=int, default=2)
    ap.add_argument("--timeout", type=int, default=5400)
    ap.add_argument("--rss-limit-gb", type=int, default=16)
    ap.add_argument("--base-config", default=os.path.join(fr.ROOT, "configs/bulk.base.json"))
    ap.add_argument("--cli", default=os.path.join(fr.ROOT, "src/FlcDataset.Cli/bin/Release/net10.0/flc-dataset"))
    ap.add_argument("--dotnet-root", default="/opt/dotnet")
    ap.add_argument("--hf-token-file", default="/srv/flc/secrets/HF_TOKEN")
    ap.add_argument("--github-token-file", default="/srv/flc/secrets/GITHUB_TOKEN")
    ap.add_argument("--keep-work", action="store_true")
    ap.add_argument("--limit", type=int, default=0, help="run: only the N smallest repositories (trial)")
    ap.add_argument("--upload", action="store_true")
    a = ap.parse_args()
    token = open(a.hf_token_file).read().strip()
    if a.stage == "select":
        select(a, token)
    elif a.stage == "run":
        run_all(a, open(a.github_token_file).read().strip())
    else:
        pack(a, token)


if __name__ == "__main__":
    main()
