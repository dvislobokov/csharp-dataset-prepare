#!/usr/bin/env python3
"""
Convert a single-GPU checkpoint of the engine's train.py into one that resumes under DDP with N ranks, continuing the data
stream exactly where it stopped (the engine refuses a world-size change: its stream state is per rank).

train.py's PackedStream: with W ranks, rank r consumes the epoch's groups r, r+W, r+2W, ... (group = cursor * W + rank).
A single-GPU stream at cursor c has consumed groups 0 .. c-1, so rank r starts at its first own group >= c:
cursor_r = ceil((c - r) / W). The documents already fetched but not emitted (queue) go to rank 0; window/token counters
are split. Model, optimizer, RNG, step and token count are kept (the global batch --tokens-per-step stays the same).

  python ckpt_to_ranks.py --in ckpt-latest.pt --out runs/go50m-ours/ckpt-latest.pt --ranks 2
"""
import argparse

import torch


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--in", dest="src", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--ranks", type=int, required=True)
    a = ap.parse_args()
    ck = torch.load(a.src, map_location="cpu", weights_only=False)
    st = ck["stream"]
    assert isinstance(st, dict), "already a multi-rank checkpoint"
    w, c = a.ranks, st["cursor"]
    ranks = []
    for r in range(w):
        ranks.append({"epoch": st["epoch"], "cursor": (c - r + w - 1) // w if c > r else 0,
                      "queue": st["queue"] if r == 0 else [], "windows": st["windows"] // w,
                      "tokens_seen": st["tokens_seen"] // w, "stats": dict(st.get("stats", {})) if r == 0 else {}})
        assert ranks[-1]["cursor"] * w + r >= c
    ck["stream"] = ranks
    torch.save(ck, a.out)
    print({"step": ck["step"], "tokens": ck["tokens"], "epoch": st["epoch"], "cursor": c,
           "rank_cursors": [x["cursor"] for x in ranks], "first_groups": [x["cursor"] * w + r for r, x in enumerate(ranks)]})


if __name__ == "__main__":
    main()
