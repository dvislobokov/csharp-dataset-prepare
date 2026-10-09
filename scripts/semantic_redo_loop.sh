#!/usr/bin/env bash
# Run stage of semantic_redo.py in rounds while the select stage is still adding repositories (each round skips repositories
# that are done; inputs of a repository are complete once its shard is selected: one repository = one shard).
set -uo pipefail
W=${W:-/srv/flc/redo}
JOBS=${JOBS:-48}
cd /srv/flc/app
while true; do
  selecting=$(pgrep -u "$(id -u)" -f "semantic_redo.py select" >/dev/null && echo 1 || echo 0)
  /srv/flc/venv/bin/python -I scripts/semantic_redo.py run --work "$W" --jobs "$JOBS" >> "$W/run.log" 2>&1
  [ "$selecting" = 0 ] && { echo '{"event": "all_rounds_done"}' >> "$W/run.log"; break; }
  sleep 60
done
