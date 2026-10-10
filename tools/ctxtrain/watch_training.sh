#!/usr/bin/env bash
# Live progress of train_lang.sh on the GPU servers, refreshed every $EVERY seconds (Ctrl+C to stop):
# stage, step / total, loss, eval perplexity, tokens/s, ETA of the current stage, GPU load and memory.
#
#   bash tools/ctxtrain/watch_training.sh            # every 60 s
#   EVERY=30 bash tools/ctxtrain/watch_training.sh
# Servers: "name|ssh target|ssh options|work dir|run name"
EVERY=${EVERY:-60}
SERVERS=(
  "Go  (H200, GPU 1; moved from the H100 at step 6000)|root@161.104.58.239||/root/flc-go|go50m-ours"
  "C#  (H200, GPU 0)|root@161.104.58.239||/root/flc-csharp|csharp50m-ours"
)

REMOTE=$(cat <<'PY'
import glob, json, os, sys, time, subprocess
w, run = sys.argv[1], sys.argv[2]
done = [s for s in ("setup", "pretrain", "finetune", "eval", "export") if os.path.exists(f"{w}/{s}.done")]
stage = next((s for s in ("setup", "pretrain", "finetune", "eval", "export") if s not in done), "finished")
def last_rows(path, n=400):
    try:
        with open(path, "rb") as f:
            f.seek(0, 2); f.seek(max(0, f.tell() - 200000))
            return [json.loads(l) for l in f.read().decode(errors="replace").splitlines()[1:] if l.startswith("{")][-n:]
    except OSError:
        return []
def fmt_eta(s):
    s = int(s); return f"{s // 3600}h{s % 3600 // 60:02d}m"
out = [f"stage: {stage}   (done: {', '.join(done) or '-'})"]
if stage in ("pretrain", "finetune"):
    mdir = f"{w}/runs/{run}" + ("-ft" if stage == "finetune" else "")
    rows = last_rows(mdir + "/metrics.jsonl")
    tr = [r for r in rows if "loss" in r and "step" in r]
    ev = [r for r in rows if "eval_ppl" in r]
    total = None
    try:
        cfg = json.load(open(mdir + "/config.json"))
        tps = 524288; total = int(float(cfg["args"]["max_tokens"]) // tps)
    except Exception:
        pass
    if tr:
        r = tr[-1]
        total = r.get("of") or total
        tok_s = r.get("tok_s") or (sum(x.get("tok_s", 0) for x in tr[-5:]) / max(len(tr[-5:]), 1))
        line = f"step {r['step']:,}" + (f" / {total:,} ({100 * r['step'] / total:.1f} %)" if total else "")
        line += f"   loss {r['loss']:.3f}   {tok_s / 1e3:,.0f}k tok/s"
        if "grad_norm" in r:
            line += f"   gnorm {r['grad_norm']:.2f}"
        if "lr" in r:
            line += f"   lr {r['lr']:.1e}"
        if total and tok_s:
            tokens_left = (total - r["step"]) * (r["tokens"] / max(r["step"], 1))
            line += f"   ETA {fmt_eta(tokens_left / tok_s)}"
        out.append(line)
    else:
        log = f"{w}/{stage}.log"
        tail = open(log, errors="replace").read().strip().splitlines()[-1:] if os.path.exists(log) else []
        out.append("starting... " + (tail[0][:110] if tail else ""))
    if ev:
        e = ev[-1]
        out.append(f"eval @ step {e.get('step', '?')}: ppl {e['eval_ppl']:.3f}" + (f"   FIM ppl {e['eval_fim_ppl']:.3f}" if "eval_fim_ppl" in e else ""))
pe = f"{w}/progress_eval.jsonl"
if os.path.exists(pe):
    rows = [json.loads(l) for l in open(pe) if l.strip()]
    ref = next((x for x in rows if x["model"] == "old"), None)
    steps = [x for x in rows if x["model"] != "old"][-6:]
    if ref or steps:
        out.append("quality on clean positions (rest of line exact / shown / precision):")
    if ref:
        out.append(f"   old plugin model   {100 * ref['exact']:.1f} % / {100 * ref['shown']:.1f} % / {100 * ref['precision']:.1f} %")
    for x in steps:
        label = ("fine-tune step" if x["model"].startswith("ft") else "language step") + f" {x['step']:,}"
        out.append(f"   {label:<22}{100 * x['exact']:.1f} % / {100 * x['shown']:.1f} % / {100 * x['precision']:.1f} %")
if stage == "eval":
    rs = [l for l in open(f"{w}/eval.log", errors="replace") if l.startswith("{\"run\"")] if os.path.exists(f"{w}/eval.log") else []
    out += [l.strip() for l in rs] or ["evaluating..."]
elif stage == "finished":
    rs = [l for l in open(f"{w}/eval.log", errors="replace") if l.startswith("{\"run\"")] if os.path.exists(f"{w}/eval.log") else []
    out += [l.strip() for l in rs]
    out += [f"model: {p}" for p in glob.glob(f"{w}/*.cml")]
try:
    g = subprocess.run(["nvidia-smi", "--query-gpu=index,utilization.gpu,memory.used,memory.total,power.draw",
                        "--format=csv,noheader,nounits"], capture_output=True, text=True, timeout=20).stdout.strip().splitlines()
    out.append("GPU: " + "  |  ".join(f"#{x.split(',')[0].strip()} {x.split(',')[1].strip()}% "
                                       f"{int(x.split(',')[2]) / 1024:.0f}/{int(x.split(',')[3]) / 1024:.0f} GB {float(x.split(',')[4]):.0f} W" for x in g))
except Exception as e:
    out.append(f"GPU: n/a ({e})")
print("\n".join("  " + l for l in out))
PY
)

while true; do
  report="$(date '+%Y-%m-%d %H:%M:%S')\n"
  for s in "${SERVERS[@]}"; do
    IFS='|' read -r name target opts wdir run <<< "$s"
    # shellcheck disable=SC2086
    body=$(timeout 40 ssh -o BatchMode=yes -o ConnectTimeout=10 $opts "$target" "python3 - '$wdir' '$run'" <<< "$REMOTE" 2>&1) \
      || body="  (no connection: ${body:0:120})"
    report+="\n== $name  $target\n$body\n"
  done
  clear
  printf "%b\n" "$report"
  echo "(refresh every ${EVERY}s, Ctrl+C to stop)"
  sleep "$EVERY"
done
