#!/usr/bin/env bash
# Search GitHub for every 5-star bucket below the original search (35..80 by default), highest first, then run the selection on each.
set -euo pipefail
APP=${APP:-/srv/flc/app}
OUT=${OUT:-/srv/flc/search}
PY=${PY:-/srv/flc/venv/bin/python}
mkdir -p "$OUT"
# Only 35..80 stars: 0..34 excluded by the user on 2026-10-09 (mostly personal/coursework repositories); set BUCKETS to override.
for lo in ${BUCKETS:-75 70 65 60 55 50 45 40 35}; do
  hi=$((lo + 4)); [ "$lo" = 75 ] && hi=80
  tag=$(printf "%02d_%02d" "$lo" "$hi")
  [ -f "$OUT/stars_$tag.done" ] && continue
  "$PY" -I "$APP/scripts/gh_search.py" --stars "$lo..$hi" --out "$OUT/stars_$tag.jsonl" >> "$OUT/search.log" 2>&1
  mkdir -p "$OUT/selection_$tag"
  "$PY" -I "$APP/data/selection/selection_script.py" "$OUT/stars_$tag.jsonl" "$OUT/selection_$tag" > "$OUT/selection_$tag/stats.json"
  touch "$OUT/stars_$tag.done"
  echo "{\"event\": \"bucket_done\", \"stars\": \"$tag\", \"selected\": $(wc -l < "$OUT/selection_$tag/selected.jsonl")}" >> "$OUT/search.log"
done
