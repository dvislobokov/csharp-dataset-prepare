#!/usr/bin/env python3
"""Deterministic draw of the clean evaluation set (5..19 stars): N repositories per star bucket, ordered by
sha256(seed + ":" + repository_id), excluding anything present in the given training state databases.

  python -I scripts/eval_fresh_sample.py --out manifest.jsonl --per-bucket 100 \
      --state /srv/flc/state/jobs.sqlite --state /srv/flc/corpus/state.sqlite \
      /srv/flc/search/selection_05_09/selected.jsonl.excluded ...
"""
import argparse
import hashlib
import json
import sqlite3


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("selections", nargs="+", help="one selected.jsonl per star bucket")
    ap.add_argument("--out", required=True)
    ap.add_argument("--per-bucket", type=int, default=100)
    ap.add_argument("--seed", default="eval-fresh-v1")
    ap.add_argument("--state", action="append", default=[])
    ap.add_argument("--min-stars", type=int, default=5)
    ap.add_argument("--max-stars", type=int, default=19)
    a = ap.parse_args()
    trained = set()
    for db in a.state:
        con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
        trained |= {r[0].lower() for r in con.execute("SELECT repo_id FROM jobs")}
    print("training repo ids:", len(trained))
    rows, seen = [], set()
    for path in a.selections:
        cand = []
        for line in open(path, encoding="utf-8"):
            r = json.loads(line)
            rid = r["repository_id"]
            if rid.lower() in trained or rid.lower() in seen or not (a.min_stars <= r["stars"] <= a.max_stars):
                continue
            cand.append((hashlib.sha256(f"{a.seed}:{rid}".encode()).hexdigest(), r))
        cand.sort(key=lambda x: x[0])
        take = [r for _, r in cand[:a.per_bucket]]
        print(path, "eligible", len(cand), "taken", len(take))
        for r in take:
            seen.add(r["repository_id"].lower())
            r = dict(r)
            r["run_tag"] = "fresh"
            rows.append(r)
    with open(a.out, "w", encoding="utf-8") as f:
        for r in sorted(rows, key=lambda r: r["repository_id"]):
            f.write(json.dumps(r, ensure_ascii=False, sort_keys=True) + "\n")
    print("wrote", len(rows), a.out)


if __name__ == "__main__":
    main()
