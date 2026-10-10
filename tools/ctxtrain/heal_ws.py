#!/usr/bin/env python3
"""
Whitespace healing of existing caret documents (flc-context healing v2, 2026-10-10).

In the editor nothing follows the caret, so `return ⟨⟩` used to end the prompt with a whitespace-only token (`return`, `␠`)
and the completion started with a word WITHOUT its space (`Check`, `Chord`...). The language model never sees that split (a space
always starts the next word's token: `␠Check`); the caret fine-tuning taught it words without a space and the space got lost
elsewhere (`newHelloReply`, `stringConnectionString`). New rule, shared with the plugin engine (ml-core healBoundary): when the
text of the line before the caret ends with horizontal whitespace after code, the healing boundary moves back over it: the prompt
ends at the code, the whitespace becomes typed text of the completion (`␠CheckChordDown(...)` — the language model's own tokens).

This tool rewrites make_docs.py output in place of a copy:
  train: <docs>/{ctx,noctx}.{tokens.u16,offsets.u64,loss_start.u32} — prompt minus its trailing whitespace tokens, completion =
         encode(whitespace + old completion text) + <|endoftext|>; documents without such an ending are copied unchanged;
  eval:  positions.jsonl — prompt_none / prompt_deps / prompt_full minus the whitespace tokens, typed = whitespace + typed.
A trailing whitespace token is "horizontal whitespace only" (spaces/tabs, no CR/LF) and preceded by a non-newline-led token —
indentation after a newline is one pretoken with the newline and stays as it is.

  python heal_ws.py --engine <idea-ml-completion> --vocab cs-16384.bpe --train <in>/train --eval <in>/eval --out <out>
"""
from __future__ import annotations

import argparse
import collections
import json
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import flcctx as F  # noqa: E402


def ws_tail(tok, ids, eot):
    """Number of trailing horizontal-whitespace tokens of a prompt that end a line of code (0: nothing to heal), and their bytes.
    Decided on the text: the trailing run of spaces/tabs must follow a non-whitespace character of the same line (indentation —
    after a newline, possibly split by BPE as `\n␠␠` + `␠␠` — is not healed) and must consist of whole tokens."""
    special = {tok.fim_prefix, tok.fim_suffix, tok.fim_middle, tok.file_sep, tok.eot, tok.r0, tok.r1, tok.r2}
    n, ws = 0, b""
    while n < len(ids) and int(ids[-1 - n]) not in special:
        b = tok.decode([int(ids[-1 - n])])
        if b and all(c in b" \t" for c in b):
            ws = b + ws
            n += 1
        else:
            break
    if n == 0 or n == len(ids) or int(ids[-1 - n]) in special:
        return 0, b""
    k = n
    while k < len(ids) and k < n + 24 and int(ids[-1 - k]) not in special:
        k += 1
    text = tok.decode([int(x) for x in ids[len(ids) - k:]])
    run = len(text) - len(text.rstrip(b" \t"))
    before = text.rstrip(b" \t")[-1:]
    if run != len(ws) or not before or before in b"\r\n":
        return 0, b""
    return n, ws


def convert_train(tok, src, dst, stats):
    eot = tok.eot
    mids = {}
    for v in ("noctx", "ctx"):
        t = np.memmap(os.path.join(src, v + ".tokens.u16"), dtype=np.uint16, mode="r")
        o = np.fromfile(os.path.join(src, v + ".offsets.u64"), dtype=np.uint64)
        ls = np.fromfile(os.path.join(src, v + ".loss_start.u32"), dtype=np.uint32)
        out_t = open(os.path.join(dst, v + ".tokens.u16.tmp"), "wb")
        offs = np.zeros(len(ls) + 1, dtype=np.uint64)
        new_ls = np.zeros(len(ls), dtype=np.uint32)
        healed = []
        for i in range(len(ls)):
            s, e, l = int(o[i]), int(o[i + 1]), int(ls[i])
            doc = t[s:e]
            n, ws = ws_tail(tok, doc[:l], eot)
            if n:
                mid_text = tok.decode([int(x) for x in doc[l:-1]])
                new_mid = tok.encode(ws + mid_text) + [eot]
                prompt = doc[:l - n]
                arr = np.concatenate([prompt, np.asarray(new_mid, dtype=np.uint16)])
                new_ls[i] = l - n
                stats[v + "_healed"] += 1
                healed.append(i)
            else:
                arr = doc
                new_ls[i] = l
            out_t.write(np.asarray(arr, dtype=np.uint16).tobytes())
            offs[i + 1] = offs[i] + len(arr)
        out_t.close()
        offs.tofile(os.path.join(dst, v + ".offsets.u64.tmp"))
        new_ls.tofile(os.path.join(dst, v + ".loss_start.u32.tmp"))
        for ext in ("tokens.u16", "offsets.u64", "loss_start.u32"):
            os.replace(os.path.join(dst, f"{v}.{ext}.tmp"), os.path.join(dst, f"{v}.{ext}"))
        stats[v + "_docs"] = len(ls)
        stats[v + "_tokens"] = int(offs[-1])
        mids[v] = set(healed)
    stats["ctx_noctx_same_healed_docs"] = mids["ctx"] == mids["noctx"]


def convert_eval(tok, src, dst, stats):
    with open(os.path.join(src, "positions.jsonl")) as f, open(os.path.join(dst, "positions.jsonl.tmp"), "w") as g:
        for line in f:
            p = json.loads(line)
            heals = [ws_tail(tok, p[k], tok.eot) for k in ("prompt_none", "prompt_deps", "prompt_full")]
            n, ws = heals[0]
            if n and "\n" not in p["typed"] and all(h == heals[0] for h in heals):
                for k, (m, _) in zip(("prompt_none", "prompt_deps", "prompt_full"), heals):
                    p[k] = p[k][:-m]
                p["typed"] = ws.decode("utf-8") + p["typed"]
                stats["eval_healed"] += 1
            elif any(h[0] for h in heals):
                stats["eval_left_inconsistent"] += 1     # e.g. a healed boundary on the previous line (typed with a newline)
            stats["eval_positions"] += 1
            g.write(json.dumps(p) + "\n")
    os.replace(os.path.join(dst, "positions.jsonl.tmp"), os.path.join(dst, "positions.jsonl"))


def verify(tok, dst, stats):
    """No prompt of the output ends with a healable whitespace token; completions single-line, ending with <|endoftext|>."""
    for v in ("noctx", "ctx"):
        t = np.memmap(os.path.join(dst, v + ".tokens.u16"), dtype=np.uint16, mode="r")
        o = np.fromfile(os.path.join(dst, v + ".offsets.u64"), dtype=np.uint64)
        ls = np.fromfile(os.path.join(dst, v + ".loss_start.u32"), dtype=np.uint32)
        left = bad = 0
        for i in range(len(ls)):
            s, e, l = int(o[i]), int(o[i + 1]), int(ls[i])
            if ws_tail(tok, t[s:s + l], tok.eot)[0]:
                left += 1
            mid = t[s + l:e]
            body = tok.decode([int(x) for x in mid[:-1]])
            if mid[-1] != tok.eot or b"\n" in body or b"\r" in body:
                bad += 1
        stats[v + "_left_unhealed"] = left
        stats[v + "_bad_completions"] = bad


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--engine", required=True)
    ap.add_argument("--vocab", required=True)
    ap.add_argument("--train", required=True)
    ap.add_argument("--eval", required=True)
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    F.engine_paths(a.engine)
    tok = F.Tok(a.vocab)
    for d in ("train", "eval"):
        os.makedirs(os.path.join(a.out, d), exist_ok=True)
    stats = collections.Counter()
    convert_train(tok, a.train, os.path.join(a.out, "train"), stats)
    convert_eval(tok, a.eval, os.path.join(a.out, "eval"), stats)
    verify(tok, os.path.join(a.out, "train"), stats)
    meta = json.load(open(os.path.join(a.train, "meta.json")))
    meta["heal_ws"] = {"rule": "trailing horizontal whitespace after code before the caret is healed into the completion (v2, 2026-10-10)",
                       **{k: (int(v) if isinstance(v, (int, np.integer)) else v) for k, v in stats.items()}}
    json.dump(meta, open(os.path.join(a.out, "train", "meta.json"), "w"), indent=1)
    print(json.dumps(meta["heal_ws"]), flush=True)
    ok = stats["noctx_left_unhealed"] == 0 and stats["ctx_left_unhealed"] == 0 and not stats["noctx_bad_completions"] \
        and not stats["ctx_bad_completions"] and stats["ctx_noctx_same_healed_docs"]
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
