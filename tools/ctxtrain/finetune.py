#!/usr/bin/env python3
"""
Fine-tune a shipped plugin model (.cml) on caret documents (make_docs.py shards), loss only on the completion part
(positions >= loss_start: typed remainder + target + <|endoftext|>). Variants ctx / noctx use the same documents in the
same order, so a pair of runs differs only by the context block / profile (docs/CONTEXT_SPEC-RU.md 10.3).

  python finetune.py --engine <idea-ml-completion> --init cs-nn-50m-e3-lr2e3.cml --docs /data/ctxdocs/train \\
      --variant ctx --out runs/cs50m-ctx --device cuda:0
Checkpoint format = the engine's train.py (`model`, `config`, `step`, `tokens`, `args`), so its eval_inline.py and
export.py work on it.
"""
from __future__ import annotations

import argparse
import json
import math
import os
import sys
import time

import numpy as np
import torch
import torch.nn.functional as Fn

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))


def batches(docs_dir, variant, micro, seed, epochs):
    pre = os.path.join(docs_dir, variant)
    toks = np.memmap(pre + ".tokens.u16", dtype=np.uint16, mode="r")
    offs = np.fromfile(pre + ".offsets.u64", dtype=np.uint64).astype(np.int64)
    loss_start = np.fromfile(pre + ".loss_start.u32", dtype=np.uint32).astype(np.int64)
    n = len(offs) - 1
    for ep in range(epochs):
        order = np.random.default_rng([seed, ep]).permutation(n)       # same permutation for both variants
        for k in range(0, n - micro + 1, micro):
            idx = order[k:k + micro]
            L = int(max(offs[i + 1] - offs[i] for i in idx))
            x = np.zeros((micro, L), dtype=np.int64)
            y = np.full((micro, L), -100, dtype=np.int64)
            for j, i in enumerate(idx):
                d = toks[offs[i]:offs[i + 1]].astype(np.int64)
                x[j, :len(d)] = d
                ls = loss_start[i]
                y[j, ls - 1:len(d) - 1] = d[ls:]                       # predict token t from position t-1
            yield torch.from_numpy(x), torch.from_numpy(y)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--engine", required=True)
    ap.add_argument("--init", required=True, help=".cml of a shipped model or a train.py ckpt-*.pt")
    ap.add_argument("--docs", required=True)
    ap.add_argument("--variant", choices=["ctx", "noctx"], required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--device", default="cuda")
    ap.add_argument("--micro", type=int, default=16, help="documents per micro-batch")
    ap.add_argument("--accum", type=int, default=2)
    ap.add_argument("--lr", type=float, default=2e-4)
    ap.add_argument("--warmup", type=int, default=100)
    ap.add_argument("--epochs", type=int, default=1)
    ap.add_argument("--max-steps", type=int, default=0)
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--compile", action="store_true")
    ap.add_argument("--save-every", type=int, default=4000, help="also keep ckpt-<step>.pt every N steps (0 = off)")
    a = ap.parse_args()
    import flcctx
    flcctx.engine_paths(a.engine)
    os.makedirs(a.out, exist_ok=True)
    torch.manual_seed(a.seed)
    if a.init.endswith(".pt"):                     # a train.py checkpoint (model trained from scratch)
        from model import CodeLM, ModelConfig
        ck = torch.load(a.init, map_location="cpu", weights_only=False)
        model = CodeLM(ModelConfig.from_dict(ck["config"]))
        model.load_state_dict(ck["model"])
        meta = {"init": a.init, "init_step": ck.get("step"), "init_tokens": ck.get("tokens")}
        del ck
    else:
        from cml_load import load_model
        model, meta = load_model(a.init)
    model.to(a.device).train()
    fwd = torch.compile(model) if a.compile else model
    opt = torch.optim.AdamW(model.param_groups(0.1), lr=a.lr, betas=(0.9, 0.95), fused=a.device.startswith("cuda"))
    n_docs = len(np.fromfile(os.path.join(a.docs, a.variant + ".loss_start.u32"), dtype=np.uint32))
    total = a.max_steps or (n_docs // a.micro * a.epochs) // a.accum
    log = open(os.path.join(a.out, "metrics.jsonl"), "a")
    step, tokens, t0, acc_loss, acc_n = 0, 0, time.time(), 0.0, 0
    it = batches(a.docs, a.variant, a.micro, a.seed, a.epochs)
    while step < total:
        lr = a.lr * min(1.0, (step + 1) / a.warmup) * (0.1 + 0.9 * 0.5 * (1 + math.cos(math.pi * min(1.0, step / max(1, total)))))
        for g in opt.param_groups:
            g["lr"] = lr
        for _ in range(a.accum):
            try:
                x, y = next(it)
            except StopIteration:
                total = step
                break
            x, y = x.to(a.device, non_blocking=True), y.to(a.device, non_blocking=True)
            with torch.autocast("cuda", dtype=torch.bfloat16, enabled=a.device.startswith("cuda")):
                logits, _ = fwd(x)
            loss = Fn.cross_entropy(logits.float().view(-1, logits.size(-1)), y.view(-1), ignore_index=-100)
            (loss / a.accum).backward()
            acc_loss += float(loss); acc_n += 1
            tokens += int(x.numel())
        torch.nn.utils.clip_grad_norm_(model.parameters(), 1.0)
        opt.step()
        opt.zero_grad(set_to_none=True)
        step += 1
        if a.save_every and step % a.save_every == 0 and step < total:     # intermediate models for ckpt_eval_loop.sh
            torch.save({"model": model.state_dict(), "config": model.config.to_dict(), "step": step, "tokens": tokens,
                        "args": {**vars(a), "no_path": False}, "init_meta": meta}, os.path.join(a.out, f"ckpt-{step}.pt"))
        if step % 20 == 0 or step == total:
            row = {"step": step, "of": total, "loss": round(acc_loss / max(acc_n, 1), 4), "lr": lr, "tokens": tokens,
                   "tok_s": round(tokens / (time.time() - t0)), "elapsed_s": round(time.time() - t0)}
            print(json.dumps(row), flush=True)
            log.write(json.dumps(row) + "\n"); log.flush()
            acc_loss, acc_n = 0.0, 0
    torch.save({"model": model.state_dict(), "config": model.config.to_dict(), "step": step, "tokens": tokens,
                "args": {**vars(a), "no_path": False}, "init_meta": meta}, os.path.join(a.out, "ckpt-latest.pt"))
    print(json.dumps({"event": "saved", "path": os.path.join(a.out, "ckpt-latest.pt"), "steps": step}), flush=True)


if __name__ == "__main__":
    main()
