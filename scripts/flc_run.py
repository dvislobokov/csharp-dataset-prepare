#!/usr/bin/env python3
"""
Bulk orchestrator: manifest -> sparse clone -> extract (syntax pass, semantic pass) -> validate -> render ->
Parquet batches -> Hugging Face upload -> delete local data.

Guarantees
  * A repository that cannot be processed is recorded as skipped/failed with a reason; the run continues.
  * A repository whose semantic pass breaks keeps what was produced (salvaged shards or a syntax-only fallback): partial.
  * Every stage is restartable: state lives in SQLite; batches are idempotent (same batch id -> same remote paths).
  * Smallest repositories first, plus a separate lane for the largest ones so they do not become the tail.
  * Checkouts and local Parquet are deleted as soon as they are no longer needed (1 TB disk budget).

Security: analyzed code is never built or executed (semantic source = adhoc, no restore/MSBuild). Tokens are read from
files, passed to git via environment (not argv/URLs) and never logged.

Run (as the unprivileged user):
  /srv/flc/venv/bin/python -I scripts/flc_run.py --manifest data/selection/selected.jsonl --jobs 60
"""
from __future__ import annotations

import argparse
import base64
import concurrent.futures as cf
import datetime as dt
import glob
import hashlib
import json
import os
import re
import shutil
import signal
import sqlite3
import struct
import subprocess
import sys
import threading
import time
import traceback
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
STOP = threading.Event()
LOG_LOCK = threading.Lock()


# ----------------------------------------------------------------------------------------------------------------- util
def now() -> str:
    return dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


class Log:
    def __init__(self, path: str):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        self.f = open(path, "a", encoding="utf-8")

    def __call__(self, event: str, **data):
        line = json.dumps({"ts": now(), "event": event, **data}, ensure_ascii=False)
        with LOG_LOCK:
            self.f.write(line + "\n")
            self.f.flush()
            if event in ("job_done", "batch_uploaded", "progress", "error", "start", "stop", "finished", "meta_done"):
                print(line, flush=True)


def slug(repo_id: str) -> str:
    return re.sub(r"[^A-Za-z0-9_.-]+", "__", repo_id)


def read_token(path: str | None) -> str | None:
    if not path or not os.path.exists(path):
        return None
    return open(path).read().strip() or None


def uniform(*parts: str) -> float:
    d = hashlib.sha256("\x1f".join(parts).encode()).digest()
    return (struct.unpack(">Q", d[:8])[0] >> 11) / float(1 << 53)


def group_rss_bytes(pgid: int) -> int:
    total = 0
    for pid in os.listdir("/proc"):
        if not pid.isdigit():
            continue
        try:
            with open(f"/proc/{pid}/stat") as f:
                fields = f.read().rsplit(")", 1)[1].split()
            if int(fields[2]) != pgid:  # field 5 (pgrp) after the comm
                continue
            with open(f"/proc/{pid}/statm") as f:
                total += int(f.read().split()[1]) * os.sysconf("SC_PAGE_SIZE")
        except (OSError, IndexError, ValueError):
            continue
    return total


def run(cmd: list[str], *, cwd=None, env=None, timeout: float, rss_limit: int, log_path: str) -> tuple[int, str]:
    """Run in its own process group; kill the group on timeout, memory limit or stop. Returns (code, reason)."""
    with open(log_path, "a", encoding="utf-8") as lf:
        lf.write(f"$ {' '.join(cmd)}\n")
        lf.flush()
        p = subprocess.Popen(cmd, cwd=cwd, env=env, stdout=lf, stderr=lf, start_new_session=True)
        start = time.time()
        reason = ""
        while True:
            try:
                code = p.wait(timeout=2)
                return code, reason
            except subprocess.TimeoutExpired:
                pass
            if STOP.is_set():
                reason = "stopped"
            elif time.time() - start > timeout:
                reason = "timeout"
            elif rss_limit and group_rss_bytes(p.pid) > rss_limit:
                reason = "memory_limit"
            if reason:
                try:
                    os.killpg(p.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                p.wait()
                return -9, reason


# ------------------------------------------------------------------------------------------------------------- state
SCHEMA = """
CREATE TABLE IF NOT EXISTS jobs (
  repo_id TEXT PRIMARY KEY, full_name TEXT, url TEXT, size_kb INTEGER, priority INTEGER, license TEXT, grp TEXT,
  split TEXT, status TEXT, reason TEXT, attempts INTEGER DEFAULT 0, revision TEXT, samples INTEGER DEFAULT 0,
  semantic INTEGER DEFAULT 0, cs_bytes INTEGER DEFAULT 0, stats TEXT, batch INTEGER, reported INTEGER DEFAULT 0,
  started TEXT, finished TEXT);
CREATE TABLE IF NOT EXISTS meta (repo_id TEXT PRIMARY KEY, fork INTEGER, archived INTEGER, parent TEXT,
  default_branch TEXT, disk_kb INTEGER, missing INTEGER DEFAULT 0);
CREATE TABLE IF NOT EXISTS file_owner (sha256 TEXT PRIMARY KEY, repo_id TEXT);
CREATE TABLE IF NOT EXISTS batches (id INTEGER PRIMARY KEY, repos TEXT, status TEXT, rows TEXT, created TEXT, uploaded TEXT);
"""
# status: pending -> running -> packed (local parquet ready) -> uploaded | skipped | failed
# outcome kept in stats.outcome: complete | partial


class State:
    def __init__(self, path: str):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        self.db = sqlite3.connect(path, check_same_thread=False, isolation_level=None, timeout=60)
        self.db.execute("PRAGMA journal_mode=WAL")
        self.lock = threading.Lock()
        with self.lock:
            self.db.executescript(SCHEMA)
            # per-job / per-batch shard tag (e.g. star bucket); older states lack the columns
            for table in ("jobs", "batches"):
                if "tag" not in {r[1] for r in self.db.execute(f"PRAGMA table_info({table})")}:
                    self.db.execute(f"ALTER TABLE {table} ADD COLUMN tag TEXT")

    def q(self, sql: str, args=()):
        with self.lock:
            return self.db.execute(sql, args).fetchall()

    def x(self, sql: str, args=()):
        with self.lock:
            self.db.execute(sql, args)


# ---------------------------------------------------------------------------------------------------------- manifest
def split_of(group: str, seed: str, eval_frac: float, test_frac: float) -> str:
    u = uniform(seed, "repo_split", group)
    return "validation" if u < eval_frac else "test" if u < eval_frac + test_frac else "train"


def load_manifest(st: State, path: str, args):
    """Several manifests may be given comma-separated; a record's optional `run_tag` names its shards (default --run-tag)."""
    n = 0
    for line in (ln for p in path.split(",") for ln in open(p, encoding="utf-8")):
        if not line.strip():
            continue
        r = json.loads(line)
        rid = r["repository_id"]
        full = rid.split("/", 1)[1]
        grp = r.get("group") or rid
        st.x("""INSERT OR IGNORE INTO jobs(repo_id, full_name, url, size_kb, priority, license, grp, split, status, tag)
                VALUES(?,?,?,?,?,?,?,?, 'pending', ?)""",
             (rid, full, r["url"], int(r.get("size_kb") or 0), int(r.get("priority") or 0), r.get("license"), grp,
              split_of(grp, args.seed, args.eval_fraction, args.test_fraction), r.get("run_tag") or args.run_tag))
        n += 1
    return n


def prefetch_meta(st: State, token: str | None, log: Log):
    """GitHub GraphQL in batches of 50: fork/archived/default branch/disk usage (one call instead of 50 REST calls)."""
    if not token:
        log("meta_skipped", reason="no_github_token")
        return
    todo = [r[0] for r in st.q("SELECT j.repo_id FROM jobs j LEFT JOIN meta m ON m.repo_id=j.repo_id WHERE m.repo_id IS NULL")]
    for i in range(0, len(todo), 50):
        if STOP.is_set():
            return
        chunk = todo[i:i + 50]
        parts = []
        for k, rid in enumerate(chunk):
            owner, name = rid.split("/")[1:3]
            parts.append(f'r{k}: repository(owner: {json.dumps(owner)}, name: {json.dumps(name)}) '
                         '{ isFork isArchived diskUsage parent { nameWithOwner } defaultBranchRef { name } }')
        body = json.dumps({"query": "query {" + " ".join(parts) + "}"}).encode()
        for attempt in range(5):
            try:
                req = urllib.request.Request("https://api.github.com/graphql", data=body,
                                             headers={"Authorization": f"Bearer {token}", "Content-Type": "application/json"})
                data = json.load(urllib.request.urlopen(req, timeout=60)).get("data") or {}
                break
            except Exception as e:  # noqa: BLE001 - network: retry with backoff
                time.sleep(2 ** attempt)
                data = None
                err = str(e)
        if data is None:
            log("meta_error", error=err[:200])
            continue
        for k, rid in enumerate(chunk):
            m = data.get(f"r{k}")
            if m is None:
                st.x("INSERT OR REPLACE INTO meta(repo_id, missing) VALUES(?, 1)", (rid,))
            else:
                st.x("INSERT OR REPLACE INTO meta VALUES(?,?,?,?,?,?,0)",
                     (rid, int(m["isFork"]), int(m["isArchived"]), (m.get("parent") or {}).get("nameWithOwner"),
                      (m.get("defaultBranchRef") or {}).get("name"), m.get("diskUsage")))
    log("meta_done", repos=len(todo))


# --------------------------------------------------------------------------------------------------------------- job
SPARSE = ["*.cs", "*.csproj", "*.sln", "*.slnx", "*.slnf", "/[Ll][Ii][Cc][Ee][Nn][CcSs][Ee]*", "/[Uu][Nn][Ll][Ii][Cc][Ee][Nn][CcSs][Ee]*", "/[Cc][Oo][Pp][Yy][Ii][Nn][Gg]*",
          "global.json", "Directory.Build.props", "Directory.Packages.props"]


def git_env(token: str | None) -> dict:
    env = {k: v for k, v in os.environ.items() if not k.startswith("GIT_")}
    env.update({"GIT_TERMINAL_PROMPT": "0", "GIT_LFS_SKIP_SMUDGE": "1"})
    if token:
        cred = base64.b64encode(f"x-access-token:{token}".encode()).decode()
        env.update({"GIT_CONFIG_COUNT": "1", "GIT_CONFIG_KEY_0": "http.https://github.com/.extraheader",
                    "GIT_CONFIG_VALUE_0": f"AUTHORIZATION: basic {cred}"})
    return env


def cli_env(args) -> dict:
    env = dict(os.environ)
    env.update({"DOTNET_ROOT": args.dotnet_root, "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_NOLOGO": "1",
                "DOTNET_gcServer": "0", "DOTNET_GCHeapHardLimit": hex(args.rss_limit_gb * 1024 ** 3 * 3 // 4)})
    return env


def cs_bytes(root: str) -> tuple[int, int]:
    total = files = 0
    for d, dirs, fs in os.walk(root):
        if ".git" in dirs:
            dirs.remove(".git")
        for f in fs:
            if f.endswith(".cs"):
                try:
                    total += os.path.getsize(os.path.join(d, f))
                    files += 1
                except OSError:
                    pass
    return total, files


def read_counters(d: str) -> dict:
    p = os.path.join(d, "run-manifest.json")
    return json.load(open(p))["counters"] if os.path.exists(p) else {}


def salvage(out_dir: str, dest: str) -> int:
    """Keep complete JSONL lines of an interrupted extract (`<out>.tmp-*/*.jsonl.partial`)."""
    tmp = sorted(glob.glob(out_dir + ".tmp-*"))
    if not tmp:
        return 0
    os.makedirs(dest, exist_ok=True)
    n = 0
    for name in ("samples", "semantic", "discovery", "exclusions"):
        src = os.path.join(tmp[-1], f"{name}.jsonl.partial")
        if not os.path.exists(src):
            continue
        with open(src, "rb") as f, open(os.path.join(dest, f"{name}.jsonl"), "wb") as o:
            for line in f:
                if not line.endswith(b"\n"):
                    break
                try:
                    json.loads(line)
                except ValueError:
                    break
                o.write(line)
                if name == "samples":
                    n += 1
    return n


def process(job: dict, args, st: State, log: Log, gh_token: str | None) -> dict:
    rid = job["repo_id"]
    jd = os.path.join(args.work, slug(rid))
    shutil.rmtree(jd, ignore_errors=True)
    os.makedirs(jd)
    lp = os.path.join(jd, "job.log")
    stats: dict = {"timings": {}}
    t0 = time.time()

    def timed(name, fn):
        s = time.time()
        r = fn()
        stats["timings"][name] = round(time.time() - s, 1)
        return r

    meta = st.q("SELECT fork, archived, parent, default_branch, disk_kb, missing FROM meta WHERE repo_id=?", (rid,))
    if meta:
        fork, archived, parent, branch, disk_kb, missing = meta[0]
        if missing:
            return {"status": "skipped", "reason": "repository_not_found"}
        if fork and args.skip_forks:
            return {"status": "skipped", "reason": f"fork_of:{parent}"}
        stats.update({"archived": bool(archived), "default_branch": branch})

    # 1. sparse, shallow, blobless clone of the default branch
    src = os.path.join(jd, "src")
    genv = git_env(gh_token)
    code, why = timed("clone", lambda: run(
        ["git", "clone", "--depth", "1", "--filter=blob:none", "--no-checkout", "--single-branch", "--", job["url"], src],
        env=genv, timeout=args.clone_timeout, rss_limit=0, log_path=lp))
    if code != 0:
        return {"status": "failed", "reason": f"clone:{why or code}", "stats": stats}
    for cmd in (["git", "-C", src, "sparse-checkout", "set", "--no-cone", *SPARSE], ["git", "-C", src, "checkout"]):
        code, why = run(cmd, env=genv, timeout=args.clone_timeout, rss_limit=0, log_path=lp)
        if code != 0:
            return {"status": "failed", "reason": f"checkout:{why or code}", "stats": stats}
    rev = subprocess.run(["git", "-C", src, "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip()
    nbytes, nfiles = cs_bytes(src)
    stats.update({"revision": rev, "cs_bytes": nbytes, "cs_files": nfiles})
    if nfiles == 0:
        return {"status": "skipped", "reason": "no_cs_files", "stats": stats, "revision": rev}
    if nbytes > args.max_cs_mb * 1024 * 1024:
        return {"status": "skipped", "reason": f"too_large_cs:{nbytes >> 20}MB", "stats": stats, "revision": rev}

    # 2. per-repository config
    base = json.load(open(args.base_config))
    base["repository_id"] = rid
    base["license"]["declared"] = job["license"]
    base["split"]["repository_split"] = {"validation": "eval"}.get(job["split"], job["split"])
    base["split"]["repository_group"] = job["grp"]
    cfg_path = os.path.join(jd, "config.json")
    json.dump(base, open(cfg_path, "w"))
    env = cli_env(args)
    rss = args.rss_limit_gb * 1024 ** 3
    if args.corpus_only:
        return process_corpus(job, args, jd, src, cfg_path, env, rss, lp, stats, rev, timed)

    def extract(out, mode_args, timeout):
        return run([args.cli, "extract", "--repo", src, "--config", cfg_path, "--out", out, "--overwrite", "--no-corpus",
                    "--workers", str(args.workers)] + mode_args, env=env, timeout=timeout, rss_limit=rss, log_path=lp)

    # 3. syntax pass: count candidates to derive a repository-wide, path-order-independent thinning
    syn = os.path.join(jd, "syntax")
    code, why = timed("syntax", lambda: extract(syn, ["--mode", "syntax_only"], args.syntax_timeout))
    if code != 0:
        return {"status": "failed", "reason": f"syntax:{why or code}", "stats": stats, "revision": rev}
    c = read_counters(syn)
    total, tests = c.get("samples.written", 0), c.get("samples.test_code", 0)
    stats.update({"candidates_written": total, "candidates_test": tests})
    if total == 0:
        reason = "license_not_allowed" if c.get("files.skipped.license_not_allowed") else "no_samples"
        return {"status": "skipped", "reason": reason, "stats": stats, "revision": rev}
    non_test = total - tests
    keep = min(1.0, args.max_samples / total)
    test_keep = 1.0
    if tests and keep * tests > args.test_share * keep * total:  # cap the share of test code
        test_keep = min(1.0, (args.test_share / (1 - args.test_share)) * non_test / tests) if non_test else 1.0
        keep = min(1.0, args.max_samples / (non_test + tests * test_keep))
    base["sampling"].update({"keep_fraction": keep, "test_keep_fraction": test_keep, "max_samples_per_repo": args.max_samples})
    json.dump(base, open(cfg_path, "w"))
    stats.update({"keep_fraction": round(keep, 5), "test_keep_fraction": round(test_keep, 5)})

    # 4. semantic pass (safe adhoc tier); salvage or syntax fallback when it breaks
    sem = os.path.join(jd, "semantic")
    outcome, data_dir = "complete", sem
    code, why = timed("semantic", lambda: extract(sem, ["--mode", "semantic_best_effort", "--semantic-source", "adhoc"],
                                                  args.semantic_timeout))
    if code != 0:
        outcome = "partial"
        stats["partial_reason"] = f"semantic:{why or code}"
        salvaged = os.path.join(jd, "salvaged")
        n = salvage(sem, salvaged)
        if n > 0:
            data_dir = salvaged
            stats["salvaged_samples"] = n
        else:
            data_dir = os.path.join(jd, "syntax_thinned")
            code, why = timed("syntax_fallback", lambda: extract(data_dir, ["--mode", "syntax_only"], args.syntax_timeout))
            if code != 0:
                return {"status": "failed", "reason": f"fallback:{why or code}", "stats": stats, "revision": rev}
    shutil.rmtree(syn, ignore_errors=True)

    # 5. validation against the checkout (exact reconstruction, schema, leakage); partial dirs have no manifest
    code, why = timed("validate", lambda: run([args.cli, "validate", "--dataset", data_dir, "--repo", src], env=env,
                                              timeout=args.syntax_timeout, rss_limit=rss, log_path=lp))
    vpath = os.path.join(data_dir, "validation.json")
    failures = json.load(open(vpath))["report"]["failures"] if os.path.exists(vpath) else {"validator": 1}
    hard = {k: v for k, v in failures.items() if not k.startswith("manifest.") and k != "sample.revision_matches_manifest"}
    if hard:
        return {"status": "failed", "reason": "validation:" + ",".join(sorted(hard))[:300], "stats": stats, "revision": rev}

    # 6. prompts
    if args.prompts:
        code, why = timed("render", lambda: run(
            [args.cli, "render", "--dataset", data_dir, "--out", os.path.join(jd, "prompts"), "--preview", "0",
             "--max-code-chars", str(args.prompt_code_chars), "--max-semantic-chars", str(args.prompt_semantic_chars)],
            env=env, timeout=args.syntax_timeout, rss_limit=rss, log_path=lp))
        if code != 0:
            stats["render_error"] = why or code

    # 7. Parquet for this repository (cross-repository exact-file dedup by first owner)
    pending = os.path.join(args.out, "pending", slug(rid))
    shutil.rmtree(pending, ignore_errors=True)
    shutil.rmtree(pending + ".tmp", ignore_errors=True)
    # Packing is JSON/Arrow-heavy Python: run it in its own process so 100 concurrent jobs are not serialised by the GIL.
    spec = json.dumps({"job": job, "rev": rev, "data_dir": data_dir, "prompt_dir": os.path.join(jd, "prompts"), "dest": pending + ".tmp"})
    code, why = timed("pack", lambda: run([sys.executable, "-I", os.path.abspath(__file__), "--pack-job", spec, "--state", args.state],
                                          timeout=args.syntax_timeout, rss_limit=rss, log_path=lp))
    done = os.path.join(pending + ".tmp", "DONE")
    if code != 0 or not os.path.exists(done):
        return {"status": "failed", "reason": f"pack:{why or code}", "stats": stats, "revision": rev}
    rows = json.load(open(done))
    os.replace(pending + ".tmp", pending)
    stats.update(rows)
    stats["outcome"] = outcome
    stats["timings"]["total"] = round(time.time() - t0, 1)
    if not args.keep_work:
        shutil.rmtree(jd, ignore_errors=True)
    return {"status": "packed", "reason": stats.get("partial_reason"), "stats": stats, "revision": rev,
            "samples": rows["samples"], "semantic": rows["semantic"]}


def process_corpus(job, args, jd, src, cfg_path, env, rss, lp, stats, rev, timed) -> dict:
    """Corpus pass: whole accepted files (same filters/licence/secret/generated/dedup rules), no caret extraction."""
    out = os.path.join(jd, "corpus")
    code, why = timed("discover", lambda: run([args.cli, "discover", "--repo", src, "--config", cfg_path, "--out", out, "--overwrite"],
                                              env=env, timeout=args.syntax_timeout, rss_limit=rss, log_path=lp))
    if code != 0:
        return {"status": "failed", "reason": f"discover:{why or code}", "stats": stats, "revision": rev}
    c = read_counters(out)
    if not c.get("files.accepted"):
        reason = "license_not_allowed" if c.get("files.skipped.license_not_allowed") else "no_accepted_files"
        return {"status": "skipped", "reason": reason, "stats": stats, "revision": rev}
    code, why = timed("validate", lambda: run([args.cli, "validate", "--dataset", out, "--repo", src], env=env,
                                              timeout=args.syntax_timeout, rss_limit=rss, log_path=lp))
    vpath = os.path.join(out, "validation.json")
    failures = json.load(open(vpath))["report"]["failures"] if os.path.exists(vpath) else {"validator": 1}
    hard = {k: v for k, v in failures.items() if k not in ("samples.missing",)}
    if hard:
        return {"status": "failed", "reason": "validation:" + ",".join(sorted(hard))[:300], "stats": stats, "revision": rev}
    pending = os.path.join(args.out, "pending", slug(job["repo_id"]))
    shutil.rmtree(pending, ignore_errors=True)
    shutil.rmtree(pending + ".tmp", ignore_errors=True)
    spec = json.dumps({"job": job, "rev": rev, "data_dir": out, "prompt_dir": "", "dest": pending + ".tmp", "corpus": True})
    code, why = timed("pack", lambda: run([sys.executable, "-I", os.path.abspath(__file__), "--pack-job", spec, "--state", args.state],
                                          timeout=args.syntax_timeout, rss_limit=rss, log_path=lp))
    done = os.path.join(pending + ".tmp", "DONE")
    if code != 0 or not os.path.exists(done):
        return {"status": "failed", "reason": f"pack:{why or code}", "stats": stats, "revision": rev}
    rows = json.load(open(done))
    os.replace(pending + ".tmp", pending)
    stats.update(rows)
    stats["outcome"] = "complete"
    stats["timings"]["total"] = round(sum(v for v in stats["timings"].values()), 1)
    if not args.keep_work:
        shutil.rmtree(jd, ignore_errors=True)
    return {"status": "packed", "reason": None, "stats": stats, "revision": rev, "samples": rows["samples"], "semantic": 0}


def pack_corpus(job, rev, data_dir, dest, st: State) -> dict:
    import pyarrow as pa
    import pyarrow.parquet as pq
    sc = schemas()["corpus"]
    os.makedirs(dest, exist_ok=True)
    rid = job["repo_id"]
    rows, dup, nbytes = [], 0, 0
    for r in jsonl(os.path.join(data_dir, "corpus.jsonl")):
        st.x("INSERT OR IGNORE INTO file_owner(sha256, repo_id) VALUES(?,?)", (r["sha256"], rid))
        if st.q("SELECT repo_id FROM file_owner WHERE sha256=?", (r["sha256"],))[0][0] != rid:
            dup += 1
            continue
        r["revision"] = rev
        r["license"] = job["license"]
        nbytes += r.get("bytes") or 0
        rows.append(pick(r, sc))
    if rows:
        pq.write_table(pa.Table.from_pylist(rows, schema=sc), os.path.join(dest, "corpus.parquet"), compression="zstd")
    counts = {"samples": len(rows), "corpus_bytes": nbytes, "cross_repo_duplicate_files": dup, "semantic": 0, "prompts": 0}
    open(os.path.join(dest, "DONE"), "w").write(json.dumps(counts))
    return counts


# ----------------------------------------------------------------------------------------------------------- parquet
def schemas():
    import pyarrow as pa
    s = pa.string()
    fact = pa.struct([("name", s), ("kind", s), ("type", s), ("nullable_annotation", s), ("signature", s),
                      ("is_static", pa.bool_()), ("overloads", pa.int32()), ("is_extension", pa.bool_())])
    samples = pa.schema([
        ("sample_id", s), ("repository_id", s), ("revision", s), ("license", s), ("relative_path", s), ("project", s),
        ("is_test", pa.bool_()), ("source_sha256", s), ("caret_utf16_offset", pa.int32()), ("caret_line_zero_based", pa.int32()),
        ("caret_column_utf16_zero_based", pa.int32()), ("caret_byte_offset", pa.int64()), ("target_end_utf16_offset", pa.int32()),
        ("target_end_byte_offset", pa.int64()), ("line_start_utf16_offset", pa.int32()), ("line_end_utf16_offset", pa.int32()),
        ("caret_kind", s), ("caret_subkind", s), ("tags", pa.list_(s)), ("left_context_start_utf16_offset", pa.int32()),
        ("left_context_truncated", pa.bool_()), ("left_context", s), ("target_text", s), ("right_context", s),
        ("right_context_end_utf16_offset", pa.int32()), ("right_context_truncated", pa.bool_()), ("end_of_line", s),
        ("indentation", s), ("quality_flags", pa.list_(s)), ("split_group", s), ("semantic_status", s),
        ("semantic_reason", s), ("config_version", s), ("generator", s)])
    semantic = pa.schema([
        ("sample_id", s), ("repository_id", s), ("visibility_policy", s), ("analysis_engine", s), ("status", s), ("reason", s),
        ("enclosing_symbol", s), ("enclosing_kind", s), ("enclosing_type", s), ("return_type", s), ("expected_type", s),
        ("expected_type_source", s), ("locals", pa.list_(fact)), ("parameters", pa.list_(fact)), ("this_members", pa.list_(fact)),
        ("receiver_type", s), ("receiver_kind", s), ("members", pa.list_(fact)),
        ("invocation_candidates", pa.list_(pa.struct([("signature", s), ("argument_index", pa.int32()), ("parameter_name", s), ("parameter_type", s)]))),
        ("context_types", pa.list_(pa.struct([("name", s), ("kind", s), ("source", s), ("is_static", pa.bool_()),
                                              ("members", pa.list_(fact)), ("total_members", pa.int32())]))),
        ("snapshot_syntax_errors", pa.int32()), ("truncated", pa.bool_()), ("prompt", s),
        ("target_identifiers", pa.list_(s)), ("covered_target_identifiers", pa.list_(s))])
    prompts = pa.schema([("sample_id", s), ("repository_id", s), ("prompt_format", s), ("caret_kind", s), ("semantic_status", s),
                         ("has_semantic", pa.bool_()), ("prompt", s), ("completion", s), ("code_truncated", pa.bool_())])
    repos = pa.schema([("repository_id", s), ("revision", s), ("license", s), ("split", s), ("group", s), ("status", s),
                       ("outcome", s), ("reason", s), ("samples", pa.int64()), ("cs_bytes", pa.int64()), ("cs_files", pa.int64()),
                       ("keep_fraction", pa.float64()), ("test_keep_fraction", pa.float64()), ("processed_utc", s)])
    corpus = pa.schema([("repository_id", s), ("revision", s), ("license", s), ("relative_path", s), ("sha256", s),
                        ("bytes", pa.int64()), ("has_bom", pa.bool_()), ("newline_style", s), ("project", s),
                        ("is_test", pa.bool_()), ("lines", pa.int32()), ("content", s)])
    return {"samples": samples, "semantic": semantic, "prompts": prompts, "repos": repos, "corpus": corpus}


def jsonl(path):
    for p in (path, path + ".gz"):
        if os.path.exists(p):
            import gzip
            with (gzip.open(p, "rt", encoding="utf-8") if p.endswith(".gz") else open(p, encoding="utf-8")) as f:
                for line in f:
                    if line.strip():
                        yield json.loads(line)
            return


def pick(row: dict, schema) -> dict:
    return {f.name: row.get(f.name) for f in schema}


def pack_repo(job, rev, data_dir, prompt_dir, dest, st: State, args) -> dict:
    import pyarrow as pa
    import pyarrow.parquet as pq
    sc = schemas()
    os.makedirs(dest, exist_ok=True)
    rid = job["repo_id"]
    # Cross-repository dedup: the first repository that claims a file hash owns its samples.
    shas = {r["sha256"] for r in jsonl(os.path.join(data_dir, "discovery.jsonl")) if r.get("status") == "accepted" and r.get("sha256")}
    for sha in shas:
        st.x("INSERT OR IGNORE INTO file_owner(sha256, repo_id) VALUES(?,?)", (sha, rid))
    owned = {r[0] for r in st.q("SELECT sha256 FROM file_owner WHERE repo_id=?", (rid,))}
    keep_ids = set()
    rows = []
    dup = 0
    for r in jsonl(os.path.join(data_dir, "samples.jsonl")):
        if shas and r["source_sha256"] not in owned:
            dup += 1
            continue
        r["license"] = job["license"]
        r["revision"] = rev
        keep_ids.add(r["sample_id"])
        rows.append(pick(r, sc["samples"]))
    counts = {"samples": len(rows), "cross_repo_duplicate_samples": dup, "semantic": 0, "prompts": 0}
    if rows:
        pq.write_table(pa.Table.from_pylist(rows, schema=sc["samples"]), os.path.join(dest, "samples.parquet"), compression="zstd")
    rows = []
    for r in jsonl(os.path.join(data_dir, "semantic.jsonl")):
        if r["sample_id"] not in keep_ids or r.get("visibility_policy") != "editor_snapshot":
            continue
        r["repository_id"] = rid
        lk = r.get("leakage") or {}
        r["target_identifiers"] = lk.get("target_identifiers")
        r["covered_target_identifiers"] = lk.get("covered_target_identifiers")
        rows.append(pick(r, sc["semantic"]))
    counts["semantic"] = len(rows)
    if rows:
        pq.write_table(pa.Table.from_pylist(rows, schema=sc["semantic"]), os.path.join(dest, "semantic.parquet"), compression="zstd")
    rows = []
    for p in sorted(glob.glob(os.path.join(prompt_dir, "prompts.*.jsonl"))):
        for r in jsonl(p):
            if r["sample_id"] in keep_ids:
                r["repository_id"] = rid
                rows.append(pick(r, sc["prompts"]))
    counts["prompts"] = len(rows)
    if rows:
        pq.write_table(pa.Table.from_pylist(rows, schema=sc["prompts"]), os.path.join(dest, "prompts.parquet"), compression="zstd")
    open(os.path.join(dest, "DONE"), "w").write(json.dumps(counts))
    return counts


# ------------------------------------------------------------------------------------------------------------ upload
def readme(st: State, args) -> str:
    rows = st.q("SELECT status, COUNT(*), SUM(samples) FROM jobs GROUP BY status")
    stats = {r[0]: {"repos": r[1], "samples": r[2] or 0} for r in rows}
    present = {}
    for cfg in ("samples", "semantic", "prompts", "corpus"):
        present[cfg] = sorted({os.path.basename(p).split("-")[0] for p in REMOTE_FILES if p.startswith(f"data/{cfg}/")})
    lines = ["---", "license: other", "license_name: per-row-source-license",
             "license_link: LICENSE.md", "pretty_name: C# full-line completion (Roslyn caret samples)",
             "task_categories:", "- text-generation", "language:", "- code", "tags:", "- code", "- csharp", "- code-completion", "configs:"]
    first = True
    for cfg, label in (("samples", "samples"), ("semantic", "semantic"), ("prompts", "prompts"), ("corpus", "corpus")):
        if not present[cfg]:
            continue
        lines += [f"- config_name: {label}"] + (["  default: true"] if first else []) + ["  data_files:"]
        first = False
        for sp in present[cfg]:
            lines += [f"  - split: {sp}", f"    path: data/{cfg}/{sp}-*.parquet"]
    # pre-tokenized corpus uploaded by scripts/tokenize_corpus.py (tokenized/<tokenizer>/corpus/<split>-*.parquet)
    tokenized = sorted({p.split("/")[1] for p in REMOTE_FILES if p.startswith("tokenized/") and "/corpus/" in p})
    for name in tokenized:
        lines += [f"- config_name: corpus_tokens_{name.split('-')[-1]}", "  data_files:"]
        for sp in sorted({os.path.basename(p).split("-")[0] for p in REMOTE_FILES if p.startswith(f"tokenized/{name}/corpus/")}):
            lines += [f"  - split: {sp}", f"    path: tokenized/{name}/corpus/{sp}-*.parquet"]
    lines += ["- config_name: repos", "  data_files:", "  - split: train", "    path: data/repos/*.parquet", "---", ""]
    body = f"""# C# full-line completion dataset (Roslyn)

Caret-based full-line completion samples extracted from permissively licensed C# repositories with
[csharp-dataset-prepare](https://github.com/dvislobokov/csharp-dataset-prepare). Each sample is an exact editor position:
`left_context` ends at the caret, `target_text` is the rest of the physical line (no newline), `right_context` follows it.
Original source is reconstructable from offsets; every row carries `repository_id`, `revision`, `relative_path` and `license`
for attribution.

| Config | Content |
|---|---|
| `samples` | canonical samples (`flc-sample/v1` fields) |
| `semantic` | Roslyn facts computed on the target-free editor snapshot (`flc-semantic/v1`, editor_snapshot policy, safe adhoc tier) |
| `prompts` | ready `flc-prompt/v2` pairs: `prompt` (special tokens as text) + `completion` ending with `<|eol|>`; loss on completion only |
| `repos` | per-repository provenance and processing status |
| `corpus` | whole accepted source files for causal pretraining (exact content, `has_bom`/`newline_style` recorded; same licence, generated-code, secret and cross-repository dedup rules) |
""" + "".join(f"| `engine/{n}/` (files, not a config) | `corpus` encoded with the plugin engine's tokenizer `{n}.bpe` in the training-shard format of idea-ml-completion (`lm` = train, `validation`, `test`: `*.tokens.u16`, `*.offsets.u64`, `*.repo.u32`, `*.files.txt`, `*.meta.json`); its `train.py --data` reads them unchanged |\n" for n in sorted({p.split("/")[1] for p in REMOTE_FILES if p.startswith("engine/") and p.count("/") >= 2})) + "".join(f"| `corpus_tokens_{n.split('-')[-1]}` | `corpus` pre-tokenized with `tokenized/{n}/tokenizer.json` (trained on corpus train only): `input_ids` uint16 per file, framed `<|cs|><|path|>`path`\\n<|code|>\\n`content`<|endoftext|>`; counts in `tokenized/{n}/stats.json` |\n" for n in tokenized) + f"""

Splits are assigned per repository group (forks/near-copies together); cross-repository duplicate files are kept once.
No code was built or executed. See the builder repository for the data contract, leakage policy and benchmarks.

""" + SEMANTIC_MD + examples_md(args) + f"""## Progress (updated each batch)

| Status | Repositories | Samples |
|---|---|---|
""" + "".join(f"| {k} | {v['repos']} | {v['samples']} |\n" for k, v in sorted(stats.items())) + f"\nLast update: {now()}\n"
    return "\n".join(lines) + body


REMOTE_FILES: set[str] = set()

SEMANTIC_MD = """## How the semantic context is produced

For every sample the builder removes the hidden target from the file (editor-equivalent snapshot) and asks Roslyn what is
visible at the caret: `RET` return type, `EXPECT` expected type, `ARG` parameters, `LOCAL` locals declared before the caret,
`FIELD`/`PROPERTY`/`METHOD` members of the current type, `RECV`/`MEMBER` receiver and its accessible members after `.`,
`CALL` overload candidates inside an argument list, `TYPE` contracts of nearby project types. Facts never come from the hidden
target (per-record leakage audit). The `samples` table is the canonical, model-agnostic record without facts; the facts live
in `semantic` (join by `sample_id`), and `prompts` already contains both rendered as training pairs.

```python
from datasets import load_dataset
repo = "dvislobokov/csharp-ml-complation"
prompts = load_dataset(repo, "prompts", split="train")    # ready prompt/completion pairs
samples = load_dataset(repo, "samples", split="train")    # canonical samples (no semantic columns)
semantic = load_dataset(repo, "semantic", split="train")  # structured Roslyn facts, same sample_id
```

"""


def pick_examples(stage: str, args) -> None:
    """Choose README examples once from real uploaded rows (one per kind of context) and persist them."""
    import pyarrow.parquet as pq
    path = os.path.join(args.out, "readme_examples.json")
    have = json.load(open(path)) if os.path.exists(path) else {}
    wanted = {"line_start": "LOCAL ", "member_access": "MEMBER ", "argument_list": "CALL ", "after_keyword": "TYPE "}
    if all(k in have for k in wanted):
        return
    pfile = sorted(glob.glob(os.path.join(stage, "data", "prompts", "train-*.parquet")))
    sfile = sorted(glob.glob(os.path.join(stage, "data", "samples", "train-*.parquet")))
    if not pfile or not sfile:
        return
    prompts = pq.read_table(pfile[0]).to_pylist()
    cols = ["sample_id", "repository_id", "relative_path", "caret_line_zero_based"]
    samples = {r["sample_id"]: r for r in pq.read_table(sfile[0], columns=cols).to_pylist()}
    for kind, marker in wanted.items():
        if kind in have:
            continue
        cands = sorted((r for r in prompts if r["caret_kind"] == kind and r["has_semantic"] and marker in r["prompt"]
                        and 400 < len(r["prompt"]) < 3500 and len(r["completion"]) < 90), key=lambda r: r["sample_id"])
        if cands:
            r = cands[0]
            m = samples.get(r["sample_id"], {})
            have[kind] = {"sample_id": r["sample_id"], "repository_id": m.get("repository_id"), "relative_path": m.get("relative_path"),
                          "line": (m.get("caret_line_zero_based") or 0) + 1, "prompt": r["prompt"], "completion": r["completion"]}
    json.dump(have, open(path, "w"), ensure_ascii=False, indent=1)


def examples_md(args) -> str:
    path = os.path.join(args.out, "readme_examples.json")
    if not os.path.exists(path):
        return ""
    have = json.load(open(path))
    titles = {"line_start": "Empty line (start of a statement)", "member_access": "After `.` (receiver members)",
              "argument_list": "Inside an argument list (overload candidates)", "after_keyword": "After a keyword (nearby project types)"}
    out = ["## Examples (real rows from the `prompts` config)", "",
           "`prompt` ends at the caret with `<|complete|>`; the model must produce `completion` (rest of the line + `<|eol|>`).",
           "Code is shortened here to its last lines; stored prompts keep up to 4,000 characters of code.", ""]
    for kind in ("line_start", "member_access", "argument_list", "after_keyword"):
        e = have.get(kind)
        if not e:
            continue
        head, _, code = e["prompt"].partition("<|code|>\n")
        tail = "\n".join(code.split("\n")[-12:])
        out += [f"### {titles[kind]}", "", f"`{e['repository_id']}` · `{e['relative_path']}:{e['line']}` · sample_id `{e['sample_id']}`", "",
                "```text", head + "<|code|>", "…", tail, "```", "", f"completion: `{e['completion']}`", ""]
    return "\n".join(out) + "\n"


LICENSE_MD = """# Licensing

Rows are derived from third-party repositories. Each row's `license` column holds the SPDX identifier of its source
repository (only permissive licenses are included: MIT, Apache-2.0, BSD-2/3-Clause, 0BSD, Unlicense, ISC, MS-PL, Zlib),
and `repository_id` + `revision` + `relative_path` identify the exact source for attribution. Redistribution must respect
the respective license terms.
"""


def make_batch(st: State, args, log: Log, final: bool) -> int | None:
    """Assign ready repositories to a new batch (recorded before writing, so a crash re-creates the same batch)."""
    ready = st.q("SELECT repo_id, samples, COALESCE(tag, ?) FROM jobs WHERE status='packed' AND batch IS NULL ORDER BY repo_id",
                 (args.run_tag,))
    # one tag per batch: take the tag with the most ready samples (or any when flushing at the end)
    by_tag: dict[str, int] = {}
    for _, n, tag in ready:
        by_tag[tag] = by_tag.get(tag, 0) + (n or 0)
    if not by_tag:
        return None
    tag = max(sorted(by_tag), key=lambda t: by_tag[t])
    ready = [(rid, n) for rid, n, t in ready if t == tag]
    total = by_tag[tag]
    if not ready or (not final and total < args.batch_samples):
        return None
    chosen, acc = [], 0
    for rid, n in ready:
        chosen.append(rid)
        acc += n or 0
        if acc >= args.batch_samples * 2:
            break
    with st.lock:
        cur = st.db.execute("INSERT INTO batches(repos, status, created, tag) VALUES(?, 'created', ?, ?)", (json.dumps(chosen), now(), tag))
        bid = cur.lastrowid
        st.db.executemany("UPDATE jobs SET batch=? WHERE repo_id=?", [(bid, r) for r in chosen])
    log("batch_created", batch=bid, repos=len(chosen), samples=acc, tag=tag)
    return bid


def write_and_upload_batch(bid: int, st: State, args, log: Log, api):
    import pyarrow as pa
    import pyarrow.parquet as pq
    repos_json, tag = st.q("SELECT repos, COALESCE(tag, ?) FROM batches WHERE id=?", (args.run_tag, bid))[0]
    repos = json.loads(repos_json)
    t_merge = time.time()
    sc = schemas()
    stage = os.path.join(args.out, "upload", f"batch-{bid:06d}")
    shutil.rmtree(stage, ignore_errors=True)
    counts = {}
    for cfg in (("corpus",) if args.corpus_only else ("samples", "semantic", "prompts")):
        tables_by_split: dict[str, list] = {}
        for rid in repos:
            split = st.q("SELECT split FROM jobs WHERE repo_id=?", (rid,))[0][0]
            p = os.path.join(args.out, "pending", slug(rid), f"{cfg}.parquet")
            if os.path.exists(p):
                tables_by_split.setdefault(split, []).append(pq.read_table(p, schema=sc[cfg]))
        for split, tables in tables_by_split.items():
            t = pa.concat_tables(tables)
            out = os.path.join(stage, "data", cfg, f"{split}-{tag}-{bid:06d}.parquet")
            os.makedirs(os.path.dirname(out), exist_ok=True)
            pq.write_table(t, out, compression="zstd", row_group_size=20000)
            counts[f"{cfg}/{split}"] = t.num_rows
    # provenance rows: this batch + all newly finished skipped/failed repositories
    rep = st.q("SELECT repo_id, revision, license, split, grp, status, stats, reason, samples, cs_bytes FROM jobs "
               "WHERE batch=? OR (status IN ('skipped','failed') AND reported=0)", (bid,))
    rrows = []
    for rid, rev, lic, split, grp, status, stats, reason, samples, csb in rep:
        s = json.loads(stats or "{}")
        rrows.append({"repository_id": rid, "revision": rev, "license": lic, "split": split, "group": grp,
                      "status": "uploaded" if status == "packed" else status, "outcome": s.get("outcome"), "reason": reason,
                      "samples": samples or 0, "cs_bytes": csb or 0, "cs_files": s.get("cs_files"),
                      "keep_fraction": s.get("keep_fraction"), "test_keep_fraction": s.get("test_keep_fraction"),
                      "processed_utc": now()})
    if not args.corpus_only:
        out = os.path.join(stage, "data", "repos", f"repos-{tag}-{bid:06d}.parquet")
        os.makedirs(os.path.dirname(out), exist_ok=True)
        pq.write_table(pa.Table.from_pylist(rrows, schema=sc["repos"]), out, compression="zstd")
    for root, _, files in os.walk(stage):
        for f in files:
            REMOTE_FILES.add(os.path.relpath(os.path.join(root, f), stage).replace(os.sep, "/"))
    try:
        pick_examples(stage, args)
    except Exception as e:  # noqa: BLE001 - README examples are cosmetic
        log("readme_examples_error", error=str(e)[:200])
    if not args.corpus_only:
        refresh_remote_files(api, args)  # picks up configs uploaded by a parallel corpus pass
        open(os.path.join(stage, "README.md"), "w").write(readme(st, args))
    open(os.path.join(stage, "LICENSE.md"), "w").write(LICENSE_MD)
    t_upload = time.time()
    stage_bytes = sum(os.path.getsize(os.path.join(r, f)) for r, _, fs in os.walk(stage) for f in fs)
    if api is not None:
        for attempt in range(8):
            try:
                api.upload_folder(repo_id=args.hf_repo, repo_type="dataset", folder_path=stage, path_in_repo=args.path_prefix or None,
                                  commit_message=f"batch {bid}: {len(repos)} repositories ({tag})")
                break
            except Exception as e:  # noqa: BLE001 - network/rate limit: exponential backoff
                log("upload_retry", batch=bid, attempt=attempt, error=str(e)[:300])
                time.sleep(min(600, 15 * 2 ** attempt))
        else:
            raise RuntimeError(f"upload failed for batch {bid}")
    with st.lock:
        st.db.execute("UPDATE batches SET status='uploaded', rows=?, uploaded=? WHERE id=?", (json.dumps(counts), now(), bid))
        st.db.execute("UPDATE jobs SET status='uploaded' WHERE batch=?", (bid,))
        st.db.executemany("UPDATE jobs SET reported=1 WHERE repo_id=?", [(r["repository_id"],) for r in rrows])
    if not args.keep_local:
        shutil.rmtree(stage, ignore_errors=True)
        for rid in repos:
            shutil.rmtree(os.path.join(args.out, "pending", slug(rid)), ignore_errors=True)
    log("batch_uploaded", batch=bid, repos=len(repos), rows=counts, remote=api is not None, mb=round(stage_bytes / 2**20, 1),
        merge_s=round(t_upload - t_merge, 1), upload_s=round(time.time() - t_upload, 1))


def refresh_remote_files(api, args):
    if api is None:
        return
    try:
        prefix = (args.path_prefix + "/") if args.path_prefix else ""
        listed = [f[len(prefix):] for f in api.list_repo_files(args.hf_repo, repo_type="dataset") if f.startswith(prefix)]
        # data/ only grows (files of the batch being staged are not remote yet); engine/ and tokenized/ are written by
        # separate scripts and may be deleted, so they mirror the remote listing exactly
        for f in [f for f in REMOTE_FILES if f.startswith(("engine/", "tokenized/"))]:
            REMOTE_FILES.discard(f)
        REMOTE_FILES.update(f for f in listed if f.startswith(("data/", "engine/", "tokenized/")))
    except Exception:  # noqa: BLE001
        pass


# -------------------------------------------------------------------------------------------------------------- main
def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--manifest")
    ap.add_argument("--pack-job", help=argparse.SUPPRESS)
    ap.add_argument("--base-config", default=os.path.join(ROOT, "configs/bulk.base.json"))
    ap.add_argument("--cli", default=os.path.join(ROOT, "src/FlcDataset.Cli/bin/Release/net10.0/flc-dataset"))
    ap.add_argument("--dotnet-root", default=os.environ.get("DOTNET_ROOT", "/opt/dotnet"))
    ap.add_argument("--work", default="/srv/flc/work")
    ap.add_argument("--out", default="/srv/flc/out")
    ap.add_argument("--state", default="/srv/flc/state/jobs.sqlite")
    ap.add_argument("--logs", default="/srv/flc/logs")
    ap.add_argument("--jobs", type=int, default=max(1, (os.cpu_count() or 2) // 2))
    ap.add_argument("--workers", type=int, default=2, help="extract workers per repository")
    ap.add_argument("--big-lane", type=float, default=0.1, help="share of job slots that take the largest repositories first")
    ap.add_argument("--max-samples", type=int, default=20000)
    ap.add_argument("--test-share", type=float, default=0.35)
    ap.add_argument("--max-cs-mb", type=int, default=400)
    ap.add_argument("--clone-timeout", type=int, default=900)
    ap.add_argument("--syntax-timeout", type=int, default=1800)
    ap.add_argument("--semantic-timeout", type=int, default=5400)
    ap.add_argument("--rss-limit-gb", type=int, default=16)
    ap.add_argument("--max-attempts", type=int, default=2)
    ap.add_argument("--skip-forks", action=argparse.BooleanOptionalAction, default=True)
    ap.add_argument("--prompts", action=argparse.BooleanOptionalAction, default=True)
    ap.add_argument("--prompt-code-chars", type=int, default=4000)
    ap.add_argument("--prompt-semantic-chars", type=int, default=1500)
    ap.add_argument("--seed", default="20261009")
    ap.add_argument("--eval-fraction", type=float, default=0.02)
    ap.add_argument("--test-fraction", type=float, default=0.02)
    ap.add_argument("--batch-samples", type=int, default=200000, help="samples per upload batch (target)")
    ap.add_argument("--hf-repo", default="dvislobokov/csharp-ml-complation")
    ap.add_argument("--hf-token-file", default="/srv/flc/secrets/HF_TOKEN")
    ap.add_argument("--github-token-file", default="/srv/flc/secrets/GITHUB_TOKEN")
    ap.add_argument("--path-prefix", default="", help="upload under this folder of the dataset repo (e.g. 'trial')")
    ap.add_argument("--run-tag", default="r1", help="part of shard names")
    ap.add_argument("--upload", action=argparse.BooleanOptionalAction, default=True)
    ap.add_argument("--limit", type=int, default=0, help="process at most N repositories (smallest first)")
    ap.add_argument("--only", nargs="*", help="process only these repository ids")
    ap.add_argument("--keep-work", action="store_true")
    ap.add_argument("--keep-local", action="store_true")
    ap.add_argument("--corpus-only", action="store_true", help="corpus pass: whole files only (no samples/semantic), config 'corpus'")
    args = ap.parse_args()
    if args.pack_job:
        spec = json.loads(args.pack_job)
        counts = (pack_corpus(spec["job"], spec["rev"], spec["data_dir"], spec["dest"], State(args.state)) if spec.get("corpus")
                  else pack_repo(spec["job"], spec["rev"], spec["data_dir"], spec["prompt_dir"], spec["dest"], State(args.state), args))
        print(json.dumps(counts))
        return
    if not args.manifest:
        ap.error("--manifest is required")

    for d in (args.work, args.out, args.logs):
        os.makedirs(d, exist_ok=True)
    log = Log(os.path.join(args.logs, "run.jsonl"))
    st = State(args.state)
    gh_token = read_token(args.github_token_file)
    api = None
    if args.upload:
        from huggingface_hub import HfApi
        api = HfApi(token=read_token(args.hf_token_file))
        refresh_remote_files(api, args)

    def on_signal(signum, _frame):
        if not STOP.is_set():
            log("stop", signal=signum)
        STOP.set()
    signal.signal(signal.SIGINT, on_signal)
    signal.signal(signal.SIGTERM, on_signal)

    n = load_manifest(st, args.manifest, args)
    # Interrupted runs: running -> pending; packed-but-unbatched stay packed; created batches are re-written/re-uploaded.
    st.x("UPDATE jobs SET status='pending' WHERE status='running'")
    log("start", manifest=n, jobs=args.jobs, workers=args.workers, hf=args.hf_repo if api else None, prefix=args.path_prefix)
    prefetch_meta(st, gh_token, log)
    for (bid,) in st.q("SELECT id FROM batches WHERE status='created' ORDER BY id"):
        write_and_upload_batch(bid, st, args, log, api)

    # Batching/upload runs in its own thread so job scheduling never waits for Parquet merging or the network.
    drain = threading.Event()
    upload_error: list[BaseException] = []

    def uploader():
        try:
            while not STOP.is_set():
                final = drain.is_set()
                bid = make_batch(st, args, log, final=final)
                if bid is not None:
                    write_and_upload_batch(bid, st, args, log, api)
                    continue
                if final:
                    return
                time.sleep(10)
        except BaseException as e:  # noqa: BLE001 - surface in main thread, stop scheduling
            upload_error.append(e)
            log("error", where="uploader", trace=traceback.format_exc()[-2000:])
            STOP.set()

    up_thread = threading.Thread(target=uploader, name="uploader", daemon=True)
    up_thread.start()

    where = "status='pending' AND attempts < ?"
    params: list = [args.max_attempts]
    if args.only:
        where += f" AND repo_id IN ({','.join('?' * len(args.only))})"
        params += args.only
    todo = st.q(f"SELECT repo_id, full_name, url, size_kb, license, grp, split FROM jobs WHERE {where} ORDER BY size_kb, repo_id", params)
    if args.limit:
        todo = todo[: args.limit]
    small = [dict(zip(("repo_id", "full_name", "url", "size_kb", "license", "grp", "split"), r)) for r in todo]
    big = list(reversed(small))
    taken: set[str] = set()
    big_slots = max(1, int(args.jobs * args.big_lane)) if len(small) > args.jobs else 0
    running: dict[cf.Future, tuple[str, bool]] = {}

    def next_job(lane_big: bool):
        src = big if lane_big else small
        while src:
            j = src.pop(0)
            if j["repo_id"] not in taken:
                taken.add(j["repo_id"])
                return j
        return None

    def run_job(job):
        rid = job["repo_id"]
        st.x("UPDATE jobs SET status='running', attempts=attempts+1, started=? WHERE repo_id=?", (now(), rid))
        try:
            res = process(job, args, st, log, gh_token)
        except Exception as e:  # noqa: BLE001 - never let one repository stop the run
            res = {"status": "failed", "reason": f"exception:{type(e).__name__}:{str(e)[:200]}", "trace": traceback.format_exc()[-2000:]}
        # Any failure while shutting down is an interruption (a killed validator/render leaves no result), not a verdict.
        if STOP.is_set() and res["status"] == "failed":
            st.x("UPDATE jobs SET status='pending' WHERE repo_id=?", (rid,))
            return res
        st.x("UPDATE jobs SET status=?, reason=?, revision=?, samples=?, semantic=?, cs_bytes=?, stats=?, finished=? WHERE repo_id=?",
             (res["status"], res.get("reason"), res.get("revision"), res.get("samples", 0), res.get("semantic", 0),
              (res.get("stats") or {}).get("cs_bytes", 0), json.dumps(res.get("stats") or {}), now(), rid))
        if res["status"] in ("failed", "skipped") and not args.keep_work:
            shutil.rmtree(os.path.join(args.work, slug(rid)), ignore_errors=True)
        log("job_done", repo=rid, status=res["status"], reason=res.get("reason"), samples=res.get("samples", 0),
            seconds=(res.get("stats") or {}).get("timings", {}).get("total"))
        if "trace" in res:
            log("error", repo=rid, trace=res["trace"])
        return res

    last_progress = 0.0
    with cf.ThreadPoolExecutor(max_workers=args.jobs) as ex:
        while not STOP.is_set():
            while len(running) < args.jobs:
                lane_big = sum(1 for _, b in running.values() if b) < big_slots
                job = next_job(lane_big) or next_job(not lane_big)
                if job is None:
                    break
                running[ex.submit(run_job, job)] = (job["repo_id"], lane_big)
            if not running:
                break
            done, _ = cf.wait(list(running), timeout=5, return_when=cf.FIRST_COMPLETED)
            for f in done:
                running.pop(f)
            if time.time() - last_progress > 60:
                last_progress = time.time()
                counts = dict(st.q("SELECT status, COUNT(*) FROM jobs GROUP BY status"))
                total_samples = st.q("SELECT COALESCE(SUM(samples),0) FROM jobs WHERE status IN ('packed','uploaded')")[0][0]
                du = shutil.disk_usage(args.work)
                log("progress", statuses=counts, samples=total_samples, running=len(running), disk_free_gb=du.free >> 30)
        if STOP.is_set():
            for f in running:
                f.cancel()
    drain.set()
    up_thread.join()
    counts = dict(st.q("SELECT status, COUNT(*) FROM jobs GROUP BY status"))
    log("stop" if STOP.is_set() else "finished", statuses=counts)


if __name__ == "__main__":
    main()
