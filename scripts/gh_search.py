#!/usr/bin/env python3
"""
GitHub repository search for one star range, in the format of data/csharp-search.jsonl.

GitHub search returns at most 1,000 results per query, so every (license, stars) query is split by repository creation
date until each slice has <= 1,000 hits. Only permissive licenses are searched (the selection allowlist); forks and
archived repositories are excluded by the query. Resumable: finished slices are recorded in <out>.slices.

  python -I scripts/gh_search.py --stars 75..80 --out data/search/stars_75_80.jsonl
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import time
import urllib.error
import urllib.request

LICENSES = ["mit", "apache-2.0", "bsd-2-clause", "bsd-3-clause", "0bsd", "unlicense", "isc", "ms-pl", "zlib"]
FIELDS = """nameWithOwner stargazerCount pushedAt diskUsage description isFork isArchived
            licenseInfo { spdxId } owner { login __typename } defaultBranchRef { name }
            repositoryTopics(first: 10) { nodes { topic { name } } }"""


class GitHub:
    def __init__(self, token: str, min_interval: float):
        self.token, self.min_interval, self.last = token, min_interval, 0.0

    def query(self, q: str, variables: dict) -> dict:
        body = json.dumps({"query": q, "variables": variables}).encode()
        for attempt in range(8):
            wait = self.min_interval - (time.time() - self.last)
            if wait > 0:
                time.sleep(wait)
            self.last = time.time()
            req = urllib.request.Request("https://api.github.com/graphql", data=body,
                                         headers={"Authorization": f"bearer {self.token}", "User-Agent": "flc-dataset-search"})
            try:
                with urllib.request.urlopen(req, timeout=60) as resp:
                    data = json.load(resp)
                    remaining = int(resp.headers.get("X-RateLimit-Remaining", "1000"))
                    reset = int(resp.headers.get("X-RateLimit-Reset", "0"))
                if remaining < 50 and reset:
                    time.sleep(max(0, reset - time.time()) + 5)
                if data.get("errors") and not data.get("data"):
                    raise RuntimeError(str(data["errors"])[:300])
                return data["data"]
            except (urllib.error.URLError, TimeoutError, RuntimeError, json.JSONDecodeError) as e:
                retry_after = getattr(e, "headers", None) and e.headers.get("Retry-After")
                delay = int(retry_after) if retry_after else min(600, 15 * 2 ** attempt)
                print(json.dumps({"event": "retry", "attempt": attempt, "delay": delay, "error": str(e)[:200]}), flush=True)
                time.sleep(delay)
        raise RuntimeError("GitHub query failed repeatedly")


SEARCH = """query($q: String!, $after: String) { search(type: REPOSITORY, query: $q, first: 40, after: $after) {
  repositoryCount pageInfo { hasNextPage endCursor } nodes { ... on Repository { %s } } } }""" % FIELDS
COUNT = "query($q: String!) { search(type: REPOSITORY, query: $q, first: 1) { repositoryCount } }"


def record(n: dict) -> dict:
    return {"full_name": n["nameWithOwner"], "stars": n["stargazerCount"], "pushed_at": n["pushedAt"],
            "size": n["diskUsage"] or 0, "license": (n.get("licenseInfo") or {}).get("spdxId"),
            "topics": [t["topic"]["name"] for t in n["repositoryTopics"]["nodes"]], "description": n["description"],
            "owner": n["owner"]["login"], "owner_type": n["owner"]["__typename"],
            "default_branch": (n.get("defaultBranchRef") or {}).get("name")}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--language", default="C#", help="GitHub language qualifier, e.g. C# or Go")
    ap.add_argument("--stars", required=True, help="GitHub range, e.g. 0..4")
    ap.add_argument("--out", required=True)
    ap.add_argument("--pushed-since", default="2024-10-09")
    ap.add_argument("--token-file", default="/srv/flc/secrets/GITHUB_TOKEN")
    ap.add_argument("--min-interval", type=float, default=1.0, help="seconds between API calls")
    args = ap.parse_args()
    gh = GitHub(open(args.token_file).read().strip(), args.min_interval)
    base = f"language:{args.language} fork:false archived:false pushed:>={args.pushed_since} stars:{args.stars}"
    done_path = args.out + ".slices"
    done = set(open(done_path).read().split("\n")) if os.path.exists(done_path) else set()
    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    out = open(args.out, "a", encoding="utf-8")
    log = open(done_path, "a")
    total = 0
    today = dt.date.today()
    for lic in LICENSES:
        stack = [(dt.date(2008, 1, 1), today)]
        while stack:
            a, b = stack.pop()
            q = f"{base} license:{lic} created:{a.isoformat()}..{b.isoformat()}"
            if q in done:
                continue
            n = gh.query(COUNT, {"q": q})["search"]["repositoryCount"]
            if n > 1000 and a < b:
                mid = a + (b - a) // 2
                stack += [(mid + dt.timedelta(days=1), b), (a, mid)]
                continue
            after, got = None, 0
            while n:
                s = gh.query(SEARCH, {"q": q, "after": after})["search"]
                for node in s["nodes"]:
                    if node and not node["isFork"] and not node["isArchived"]:
                        out.write(json.dumps(record(node), ensure_ascii=False) + "\n")
                        got += 1
                if not s["pageInfo"]["hasNextPage"]:
                    break
                after = s["pageInfo"]["endCursor"]
            out.flush()
            log.write(q + "\n")
            log.flush()
            total += got
            print(json.dumps({"event": "slice", "license": lic, "created": f"{a}..{b}", "count": n, "written": got, "total": total}), flush=True)
    print(json.dumps({"event": "done", "stars": args.stars, "written": total}), flush=True)


if __name__ == "__main__":
    main()
