#!/usr/bin/env bash
# C# expansion 2026-10-10: every star bucket 0..34 (previously excluded), eval-fresh repositories removed; samples pass and
# corpus pass in parallel, shards tagged by bucket (s00_04 ... s30_34). Repositories already in a state DB are ignored.
set -uo pipefail
S=/srv/flc/search; PY=/srv/flc/venv/bin/python
cd /srv/flc/app
export DOTNET_ROOT=/opt/dotnet PATH=/opt/dotnet:$PATH
m=$S/manifest.expand.jsonl
$PY -I - $S /srv/flc/eval-fresh/manifest.jsonl > $m <<'PY'
import json, os, sys
s, ev = sys.argv[1], sys.argv[2]
excl = {json.loads(l)["repository_id"] for l in open(ev)}
seen = set()
for tag in ("30_34", "25_29", "20_24", "15_19", "10_14", "05_09", "00_04"):
    d = os.path.join(s, f"selection_{tag}")
    f = os.path.join(d, "selected.jsonl")
    if not os.path.exists(f) or tag != "00_04":
        f = os.path.join(d, "selected.jsonl.excluded")
    for l in open(f):
        r = json.loads(l)
        if r["repository_id"] in excl or r["repository_id"] in seen: continue
        seen.add(r["repository_id"]); r["run_tag"] = "s" + tag
        print(json.dumps(r, ensure_ascii=False))
PY
echo "{\"event\": \"expand_start\", \"repos\": $(wc -l < $m)}" >> $S/expand.log
$PY -I scripts/flc_run.py --manifest $m --jobs 56 --workers 2 --run-tag lowstars >> /srv/flc/logs/stdout.log 2>&1 &
a=$!
$PY -I scripts/flc_run.py --manifest $m --jobs 24 --workers 2 --corpus-only --run-tag lowstars \
  --work /srv/flc/corpus/work --out /srv/flc/corpus/out --state /srv/flc/corpus/state.sqlite --logs /srv/flc/corpus/logs \
  >> /srv/flc/corpus/stdout.log 2>&1 &
b=$!
wait $a $b
echo '{"event": "expand_done"}' >> $S/expand.log
