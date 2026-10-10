#!/usr/bin/env bash
# Live progress of train_lang.sh on the GPU servers, refreshed every $EVERY seconds (Ctrl+C to stop):
# stage, step / total, loss, eval perplexity, tokens/s, ETA of the current stage, GPU load and memory.
#
#   bash tools/ctxtrain/watch_training.sh            # every 60 s
#   EVERY=30 bash tools/ctxtrain/watch_training.sh
# Servers: "name|ssh target|ssh options|work dir|run name"
EVERY=${EVERY:-60}
SERVERS=(
  "Go  (2x H200): language model done, caret fine-tune lr 5e-5 on both GPUs|root@161.104.58.239||/root/flc-go|go50m-ours"
  "C#  (H200): first model (fine-tune lr 2e-4), finished|root@161.104.58.239||/root/flc-csharp|csharp50m-ours"
  "C#  (H100): final fine-tune lr 5e-5 (full) + short lr tests|ubuntu@195.209.208.156|-i $HOME/.ssh/id_immer|/home/ubuntu/flc-cs|-"
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
if run == "-":                                       # hypothesis runs: several fine-tunes side by side
    out = []
    for sub in ("runs", "runs-short"):
        names = sorted(os.listdir(f"{w}/{sub}")) if os.path.isdir(f"{w}/{sub}") else []
        for name in [n for n in names if os.path.isdir(f"{w}/{sub}/{n}")]:
            mpath = f"{w}/{sub}/{name}/metrics.jsonl"
            tr = [r for r in last_rows(mpath) if "loss" in r]
            if not tr:
                continue
            r = tr[-1]; recent = [x["loss"] for x in tr[-10:]]
            if os.path.exists(f"{w}/{sub}/{name}/done") or r["step"] >= r["of"]:
                state = "done"
            elif time.time() - os.path.getmtime(mpath) > 300:
                state = "stopped"
            else:
                state = f"{r['tok_s'] / 1e3:,.0f}k tok/s"
            kind = "short" if sub == "runs-short" else "full"
            out.append(f"{kind:5s} {name:9s} step {r['step']:,} / {r['of']:,} ({100 * r['step'] / r['of']:.0f} %)   "
                       f"loss {sum(recent) / len(recent):.3f}   {state}")
    stage = "hyp"
if stage == "hyp":
    pass
elif stage in ("pretrain", "finetune"):
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
        speeds = sorted(x["tok_s"] for x in tr[-11:] if x.get("tok_s"))      # median: rows that include an eval are slow
        tok_s = speeds[len(speeds) // 2] if speeds else 0
        line = f"step {r['step']:,}" + (f" / {total:,} ({100 * r['step'] / total:.1f} %)" if total else "")
        recent = [x["loss"] for x in tr[-10:]]                 # one row = 10-20 steps: average the last ~10 rows
        line += f"   loss {sum(recent) / len(recent):.3f} (avg last {len(recent)} rows)   {tok_s / 1e3:,.0f}k tok/s"
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
    steps = [x for x in rows if x["model"] != "old"][-12:]
    if ref or steps:
        out.append("quality on clean positions (rest of line exact / shown / precision):")
    if ref:
        out.append(f"   old plugin model   {100 * ref['exact']:.1f} % / {100 * ref['shown']:.1f} % / {100 * ref['precision']:.1f} %")
    for x in steps:
        m = x["model"]
        label = ("fine-tune step" if m.startswith("ft") else "language step" if m.startswith("step") else m.rsplit("-", 1)[0]) + f" {x['step']:,}"
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
