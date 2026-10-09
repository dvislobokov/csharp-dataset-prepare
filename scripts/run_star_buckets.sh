#!/usr/bin/env bash
# Process the star-bucket selections produced by search_star_buckets.sh: samples pass and corpus pass in parallel, each
# repository's shards tagged with its bucket (s75_80, ...). Repeats until every bucket is searched and processed;
# repositories already in a state database are ignored (INSERT OR IGNORE), so rounds only add new buckets.
set -uo pipefail
APP=${APP:-/srv/flc/app}
S=${S:-/srv/flc/search}
PY=${PY:-/srv/flc/venv/bin/python}
JOBS=${JOBS:-48}
CJOBS=${CJOBS:-24}
cd "$APP"
export DOTNET_ROOT=/opt/dotnet PATH=/opt/dotnet:$PATH
while true; do
  buckets=$(ls "$S"/stars_*.done 2>/dev/null | sed 's/.*stars_\(.*\)\.done/\1/' | sort)
  n_done=$(echo "$buckets" | grep -c . || true)
  m="$S/manifest.round.jsonl"
  for b in $buckets; do
    "$PY" -I -c 'import json,sys
for l in open(sys.argv[1]):
    r=json.loads(l); r["run_tag"]="s"+sys.argv[2]; print(json.dumps(r,ensure_ascii=False))' "$S/selection_$b/selected.jsonl" "$b"
  done > "$m"
  echo "{\"event\": \"round\", \"buckets\": \"$(echo $buckets)\", \"repos\": $(wc -l < "$m")}"
  "$PY" -I scripts/flc_run.py --manifest "$m" --jobs "$JOBS" --workers 2 --run-tag lowstars >> /srv/flc/logs/stdout.log 2>&1 &
  a=$!
  "$PY" -I scripts/flc_run.py --manifest "$m" --jobs "$CJOBS" --workers 2 --corpus-only --run-tag lowstars \
    --work /srv/flc/corpus/work --out /srv/flc/corpus/out --state /srv/flc/corpus/state.sqlite --logs /srv/flc/corpus/logs \
    >> /srv/flc/corpus/stdout.log 2>&1 &
  b=$!
  wait $a $b
  [ "$n_done" -ge "${N_BUCKETS:-9}" ] && { echo '{"event": "all_buckets_processed"}'; break; }
  # wait for at least one new bucket before the next round
  until [ "$(ls "$S"/stars_*.done 2>/dev/null | wc -l)" -gt "$n_done" ]; do sleep 60; done
done
