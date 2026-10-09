#!/usr/bin/env python3
"""
Evaluate models on make_docs.py --eval positions in the prompt modes none / deps / full, with the engine's own greedy
decoder (eval_inline.Generator: typed-remainder constraint, stop on a newline-led or special token, repetition guard).
Metrics: rest-of-line exact (generated text minus the typed remainder == target, trailing spaces ignored), shown rate
and precision at the plugin gate (confProd >= 0.7, 0.5 after a dot), per caret kind; paired bootstrap of differences.

  python eval_ctx.py --engine <idea-ml-completion> --vocab cs-16384.bpe --positions eval/positions.jsonl \\
      --model A=runs/cs50m-ctx/ckpt-latest.pt:none,deps,full --model B=runs/cs50m-noctx/ckpt-latest.pt:none \\
      --model old=cs-nn-50m-e3-lr2e3.cml:none --out eval/report.json --device cuda:0
"""
from __future__ import annotations

import argparse
import collections
import json
import os
import random
import sys

import numpy as np
import torch

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))


def load(path, device):
    if path.endswith(".cml"):
        from cml_load import load_model
        m, _ = load_model(path)
    else:
        from model import CodeLM, ModelConfig
        ck = torch.load(path, map_location="cpu", weights_only=False)
        m = CodeLM(ModelConfig.from_dict(ck["config"]))
        m.load_state_dict(ck["model"])
    return m.to(device).eval()


def run(model, tok, positions, mode, device, batch):
    import eval_inline as E
    gen = E.Generator(model, tok, device)
    out = []
    for k in range(0, len(positions), batch):
        part = positions[k:k + batch]
        with torch.no_grad():
            res = gen.generate([p["prompt_" + mode] for p in part], 48,
                               constraints=[p["typed"].encode("utf-8", "surrogateescape") for p in part], rep_guard=True)
        for p, (ids, probs, stop_p, kind, _, _) in zip(part, res):
            g = tok.decode(ids)
            typed = p["typed"].encode("utf-8", "surrogateescape")
            g = g[len(typed):] if g.startswith(typed) else g
            allp = probs + ([stop_p] if stop_p is not None else [])
            conf = float(np.prod(allp)) if allp and kind not in ("repeat", "limit") else 0.0
            exact = g.decode("utf-8", "replace").rstrip() == p["target"].rstrip() and len(g.strip()) > 0
            dot = p["typed"] == "" and p.get("after_dot", False)
            out.append({"exact": exact, "conf": conf, "kind": p["caret_kind"], "dot": p["caret_kind"] in ("member_access",) or dot,
                        "gen": g.decode("utf-8", "replace")})
    return out


def summary(rs):
    n = len(rs)
    shown = [r for r in rs if r["conf"] >= (0.5 if r["dot"] else 0.7)]
    return {"n": n, "exact": round(sum(r["exact"] for r in rs) / max(n, 1), 4),
            "shown": round(len(shown) / max(n, 1), 4), "precision": round(sum(r["exact"] for r in shown) / max(len(shown), 1), 4)}


def bootstrap(a, b, iters=2000, seed=1):
    """Paired bootstrap of exact(a) - exact(b): mean and 95 % interval."""
    x = np.array([r["exact"] for r in a], float) - np.array([r["exact"] for r in b], float)
    rng = np.random.default_rng(seed)
    means = [x[rng.integers(0, len(x), len(x))].mean() for _ in range(iters)]
    return {"diff": round(float(x.mean()), 4), "ci95": [round(float(np.percentile(means, 2.5)), 4), round(float(np.percentile(means, 97.5)), 4)]}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--engine", required=True)
    ap.add_argument("--vocab", required=True)
    ap.add_argument("--positions", required=True)
    ap.add_argument("--model", action="append", required=True, help="NAME=path(.pt|.cml):mode,mode")
    ap.add_argument("--out", required=True)
    ap.add_argument("--device", default="cuda")
    ap.add_argument("--batch", type=int, default=64)
    ap.add_argument("--limit", type=int, default=0)
    a = ap.parse_args()
    import flcctx
    flcctx.engine_paths(a.engine)
    import eval_inline as E
    tok = E.Tokenizer(a.vocab)
    positions = [json.loads(l) for l in open(a.positions)]
    if a.limit:
        positions = positions[: a.limit]
    results = {}
    for spec in a.model:
        name, rest = spec.split("=", 1)
        path, modes = rest.rsplit(":", 1)
        m = load(path, a.device)
        for mode in modes.split(","):
            results[f"{name}:{mode}"] = run(m, tok, positions, mode, a.device, a.batch)
            print(json.dumps({"run": f"{name}:{mode}", **summary(results[f'{name}:{mode}'])}), flush=True)
        del m
        torch.cuda.empty_cache()
    report = {"positions": len(positions), "runs": {k: summary(v) for k, v in results.items()}, "by_kind": {}, "paired": {}}
    kinds = sorted({p["caret_kind"] for p in positions})
    for k, v in results.items():
        report["by_kind"][k] = {kind: summary([r for r in v if r["kind"] == kind]) for kind in kinds}
    names = list(results)
    for i in range(len(names)):
        for j in range(i + 1, len(names)):
            report["paired"][f"{names[i]} - {names[j]}"] = bootstrap(results[names[i]], results[names[j]])
    facts_only = [i for i, p in enumerate(positions) if p.get("has_facts")]
    report["paired_on_positions_with_facts"] = {
        f"{names[i]} - {names[j]}": bootstrap([results[names[i]][x] for x in facts_only], [results[names[j]][x] for x in facts_only])
        for i in range(len(names)) for j in range(i + 1, len(names))} if facts_only else {}
    json.dump(report, open(a.out, "w"), indent=1)
    with open(os.path.splitext(a.out)[0] + ".samples.jsonl", "w") as f:
        for i, p in enumerate(positions[:2000]):
            f.write(json.dumps({"sample_id": p["sample_id"], "kind": p["caret_kind"], "target": p["target"],
                                **{k: v[i]["gen"] for k, v in results.items()}}) + "\n")
    print(json.dumps({"event": "done", "out": a.out}), flush=True)


if __name__ == "__main__":
    main()
