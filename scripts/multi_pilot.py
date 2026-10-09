#!/usr/bin/env python3
"""Multi-repository pilot: per-repo config, E0 discover, E1 syntax, semantic (safe adhoc tier), validation, render, report.

Untrusted repositories: no restore, no MSBuild evaluation (--semantic-source adhoc). Run from the project root:
    python3 -I scripts/multi_pilot.py artifacts/multi/repos.txt
repos.txt lines: "<owner>/<name> <branch>" (checked out under data/repos/<name> via `flc-dataset pilot fetch`).
"""
import json, os, subprocess, sys, time

ROOT = os.getcwd()
CLI = os.path.join(ROOT, "src/FlcDataset.Cli/bin/Release/net10.0/flc-dataset")
OUT = os.path.join(ROOT, "artifacts/multi")
BASE = json.load(open(os.path.join(ROOT, "configs/eshop.pilot.json")))
WORKERS = os.environ.get("FLC_WORKERS", "4")


def config_for(full_name, license_spdx):
    c = json.loads(json.dumps(BASE))
    c["config_version"] = "multi-pilot/1"
    c["repository_id"] = "github.com/" + full_name
    c["license"]["declared"] = license_spdx
    c["discovery"]["include"] = ["**/*.cs"]
    c["discovery"]["exclude"] = ["**/obj/**", "**/bin/**", "**/.git/**", "**/node_modules/**",
                                 "**/Library/**", "**/Temp/**"]  # Unity caches
    c["split"] = {"group_by": "project_family", "family_pattern": "^([^.]+)", "eval_fraction": 0.15,
                  "test_fraction": 0.0, "overrides": {}}
    c["semantic"]["solution"] = None
    c["semantic"]["subset_fraction"] = 1.0
    return c


def run(args, log):
    t = time.time()
    p = subprocess.run([CLI] + args, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    with open(log, "a") as f:
        f.write("$ flc-dataset " + " ".join(args) + "\n" + p.stderr[-20000:] + "\n")
    return p.returncode, time.time() - t


def manifest(d):
    p = os.path.join(d, "run-manifest.json")
    return json.load(open(p)) if os.path.exists(p) else None


def main(repos_file):
    meta = {}
    for line in open(os.path.join(ROOT, "data/csharp-search.jsonl")):
        r = json.loads(line)
        meta.setdefault(r["full_name"], r)
    rows = []
    for line in open(repos_file):
        if not line.strip():
            continue
        full, _branch = line.split()
        name = full.split("/")[1]
        repo = os.path.join(ROOT, "data/repos", name)
        cfg_path = os.path.join(ROOT, "configs/multi", name + ".json")
        json.dump(config_for(full, meta[full]["license"]), open(cfg_path, "w"), indent=2)
        base = os.path.join(OUT, name)
        log = os.path.join(OUT, name + ".log")
        open(log, "w").close()
        res = {"repo": full, "category": None}
        for mode, d, extra in [
            ("syntax", base + "-syntax", ["--workers", WORKERS]),
            ("semantic_adhoc", base + "-adhoc", ["--workers", WORKERS, "--mode", "semantic_best_effort", "--semantic-source", "adhoc"]),
        ]:
            code, secs = run(["extract", "--repo", repo, "--config", cfg_path, "--out", d, "--overwrite"] + extra, log)
            vcode, vsecs = run(["validate", "--dataset", d, "--repo", repo], log) if code == 0 else (None, 0)
            res[mode] = {"exit": code, "process_s": round(secs, 1), "validate_exit": vcode, "validate_s": round(vsecs, 1),
                         "dir": os.path.relpath(d, ROOT)}
        rcode, _ = run(["render", "--dataset", base + "-adhoc", "--out", base + "-prompts", "--preview", "30"], log)
        res["render_exit"] = rcode
        rows.append(res)
        print(json.dumps(res), flush=True)
    json.dump(rows, open(os.path.join(OUT, "multi-runs.json"), "w"), indent=2)


if __name__ == "__main__":
    main(sys.argv[1])
