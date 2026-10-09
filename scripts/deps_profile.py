#!/usr/bin/env python3
"""
Project dependency profiles (`DEPS` line of docs/CONTEXT_SPEC-RU.md, section 4) from the corpus config.

Per repository: for every file, the set of external library roots it imports (C#: using directives; Go: import paths),
with the repository's own namespaces/module and the standard library removed. The profile of a file is computed from the
OTHER files of its repository: roots used in >= 2 other files, by file count (desc) then name, top 12, <= 200 chars.

Outputs (parquet, zstd):
  file_roots.parquet  repository_id, relative_path, sha256, roots (list<string>)  -- per corpus file
  repo_roots.parquet  repository_id, root, files                                 -- per repository root counts
and summary.json (coverage, top roots). `profile_for(repo_counts, file_roots)` gives the DEPS items of one file.

  python -I scripts/deps_profile.py --lang csharp --corpus /srv/flc/engine-shards/src/data/corpus --out /srv/flc/deps/csharp
"""
from __future__ import annotations

import argparse
import collections
import glob
import json
import multiprocessing as mp
import os
import re

MAX_ITEMS, MAX_CHARS, MIN_FILES = 12, 200, 2

# ---------------------------------------------------------------------------------------------------------------- C#
CS_USING = re.compile(r"^[ \t]*(?:global[ \t]+)?using[ \t]+(?:static[ \t]+)?(?:[A-Za-z_]\w*[ \t]*=[ \t]*)?"
                      r"(?:global::)?([A-Za-z_][\w.]*)[ \t]*;", re.M)
CS_NAMESPACE = re.compile(r"^[ \t]*namespace[ \t]+([A-Za-z_][\w.]*)", re.M)


def cs_root(ns: str) -> str | None:
    parts = ns.split(".")
    if parts[0] == "System":
        return None
    if parts[0] == "Microsoft" and len(parts) >= 2 and parts[1] in ("Extensions", "AspNetCore"):
        return ".".join(parts[:3])
    if parts[0] in ("Microsoft", "Google", "Amazon", "Azure"):
        return ".".join(parts[:2])
    return parts[0]


def cs_own(ns: str, declared: set[str]) -> bool:
    """A namespace of the repository itself: declared there, or a prefix / extension of a declared one."""
    for d in declared:
        if ns == d or ns.startswith(d + ".") or d.startswith(ns + "."):
            return True
    return False


# ---------------------------------------------------------------------------------------------------------------- Go
GO_IMPORT_ONE = re.compile(r'^[ \t]*import[ \t]+(?:[\w.]+[ \t]+)?"([^"]+)"', re.M)
GO_IMPORT_BLOCK = re.compile(r"^[ \t]*import[ \t]*\((.*?)\)", re.M | re.S)
GO_BLOCK_LINE = re.compile(r'^[ \t]*(?:[\w.]+[ \t]+)?"([^"]+)"', re.M)
GO_VERSION = re.compile(r"/v\d+$")


def go_imports(src: str) -> set[str]:
    out = set(GO_IMPORT_ONE.findall(src))
    for block in GO_IMPORT_BLOCK.findall(src):
        out.update(GO_BLOCK_LINE.findall(block))
    return out


def go_root(path: str) -> str | None:
    parts = path.split("/")
    if "." not in parts[0]:
        return None  # standard library (and "C")
    n = 3 if parts[0] in ("github.com", "gitlab.com", "bitbucket.org") or (parts[0] == "golang.org" and parts[1:2] == ["x"]) else 2
    return GO_VERSION.sub("", "/".join(parts[:n]))


def go_module_path(repo_id: str, dirs: set[str], imports: collections.Counter) -> str | None:
    """The module path is not in the corpus (no go.mod): the repository id when imports use it, otherwise the most frequent
    prefix P such that an import is P + "/" + <a directory of the repository> (vanity paths such as go.uber.org/zap)."""
    rid = repo_id.lower()
    if any(p.lower() == rid or p.lower().startswith(rid + "/") for p in imports):
        return rid
    cand = collections.Counter()
    for p, n in imports.items():
        for d in dirs:
            if d and p.endswith("/" + d):
                cand[p[: -len(d) - 1]] += n
    return cand.most_common(1)[0][0] if cand else None


# ------------------------------------------------------------------------------------------------------------ common
def _work(args):
    import pyarrow.parquet as pq
    lang, path, rg = args
    t = pq.ParquetFile(path).read_row_group(rg, columns=["repository_id", "relative_path", "sha256", "content"])
    out = []
    for r, p, s, c in zip(*(t.column(i).to_pylist() for i in range(4))):
        c = c or ""
        if lang == "csharp":
            out.append((r, p, s, sorted(set(CS_USING.findall(c))), sorted(set(CS_NAMESPACE.findall(c)))))
        else:
            out.append((r, p, s, sorted(go_imports(c)), []))
    return out


def profile_for(repo_counts: dict[str, int], own_roots: list[str]) -> list[str]:
    """DEPS items of one file: roots in >= MIN_FILES OTHER files of the repository, top MAX_ITEMS within MAX_CHARS."""
    mine = set(own_roots)
    items = [(c - (1 if r in mine else 0), r) for r, c in repo_counts.items()]
    items = sorted(((n, r) for n, r in items if n >= MIN_FILES), key=lambda x: (-x[0], x[1]))[:MAX_ITEMS]
    out, size = [], len("DEPS")
    for _, r in items:
        if size + 1 + len(r) > MAX_CHARS:
            break
        out.append(r)
        size += 1 + len(r)
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--lang", choices=["csharp", "go"], required=True)
    ap.add_argument("--corpus", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--workers", type=int, default=24)
    a = ap.parse_args()
    import pyarrow as pa
    import pyarrow.parquet as pq
    os.makedirs(a.out, exist_ok=True)
    files = sorted(glob.glob(os.path.join(a.corpus, "*.parquet")))
    units = [(a.lang, f, rg) for f in files for rg in range(pq.ParquetFile(f).num_row_groups)]
    by_repo: dict[str, list] = collections.defaultdict(list)
    with mp.Pool(a.workers) as pool:
        for rows in pool.imap(_work, units, chunksize=1):
            for r in rows:
                by_repo[r[0]].append(r)
    fr_rows, rr_rows = [], []
    root_repos, covered, total = collections.Counter(), 0, 0
    for rid in sorted(by_repo):
        rows = by_repo[rid]
        if a.lang == "csharp":
            declared = {ns for row in rows for ns in row[4]}
            def roots_of(imps):
                return sorted({x for x in (cs_root(n) for n in imps if not cs_own(n, declared)) if x})
        else:
            dirs = {os.path.dirname(row[1]) for row in rows}
            mod = go_module_path(rid, dirs, collections.Counter(p for row in rows for p in row[3]))
            def roots_of(imps):
                return sorted({x for x in (go_root(p) for p in imps
                                           if not (mod and (p == mod or p.startswith(mod + "/")))) if x})
        per_file = [(row[1], row[2], roots_of(row[3])) for row in rows]
        counts = collections.Counter(r for _, _, rs in per_file for r in rs)
        for root, n in sorted(counts.items()):
            rr_rows.append({"repository_id": rid, "root": root, "files": n})
            root_repos[root] += 1
        for path, sha, rs in per_file:
            fr_rows.append({"repository_id": rid, "relative_path": path, "sha256": sha, "roots": rs})
            total += 1
            covered += bool(profile_for(counts, rs))
    pq.write_table(pa.Table.from_pylist(fr_rows), os.path.join(a.out, "file_roots.parquet"), compression="zstd")
    pq.write_table(pa.Table.from_pylist(rr_rows), os.path.join(a.out, "repo_roots.parquet"), compression="zstd")
    summary = {"lang": a.lang, "repositories": len(by_repo), "files": total,
               "files_with_nonempty_profile": covered, "share_nonempty": round(covered / max(total, 1), 4),
               "rules": {"max_items": MAX_ITEMS, "max_chars": MAX_CHARS, "min_other_files": MIN_FILES},
               "top_roots_by_repositories": root_repos.most_common(60)}
    json.dump(summary, open(os.path.join(a.out, "summary.json"), "w"), indent=1)
    print(json.dumps({k: v for k, v in summary.items() if k != "top_roots_by_repositories"}), flush=True)


if __name__ == "__main__":
    main()
