"""Parity of flcctx with the engine harness (eval_inline) on real corpus files, and the v1.1 split invariant."""
import glob, random, sys, os, json
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
import flcctx as F
eng, vocab, corpus = sys.argv[1], sys.argv[2], sys.argv[3]
F.engine_paths(eng)
import eval_inline as E
import pyarrow.parquet as pq
tok, etok = F.Tok(vocab), E.Tokenizer(vocab)
rnd = random.Random(7)
f = sorted(glob.glob(os.path.join(corpus, "validation-*.parquet")))[0]
rows = pq.read_table(f, columns=["content"]).column(0).to_pylist()
rnd.shuffle(rows)
c = {"positions": 0, "boundary_eq": 0, "tail_eq": 0, "split_ok": 0, "blank_above": 0}
for content in rows[:400]:
    text = content.encode().replace(b"\r\n", b"\n")
    for _ in range(5):
        if len(text) < 50:
            break
        caret = rnd.randrange(1, len(text))
        bol = text.rfind(b"\n", 0, caret) + 1
        eol, _ = F.line_end(text, caret)
        for mode in ("boundary", "word", "word-eol"):
            E.HEAL_MODE = mode
            c["boundary_eq"] += F.pretoken_boundary(tok, text, bol, caret, eol, mode) == E.pretoken_boundary(text, bol, caret, eol)
        ids = tok.encode(text[:caret])
        for tgt in (512, 896, 1024):
            c["tail_eq"] += F.stable_tail(tok, ids, tgt, tgt + 200) == E.stable_tail(etok, ids, tgt, tgt + 200)
        p = F.build(tok, text, caret, "src/A.cs", [tok.r0] + tok.encode(b"Serilog"), [tok.r1] + tok.encode(b"RET int") + [tok.r2])
        c["split_ok"] += p.split_ok
        c["blank_above"] += text[max(0, bol - 2):bol] == b"\n\n"
        # without any context the prompt must equal the engine's SPM prompt with the same budgets
        q = F.build(tok, text, caret, "src/A.cs", reserve_context=False)
        pos = {"text": text, "cursor": caret, "boundary": q.boundary, "eol": eol, "path": "src/A.cs"}
        c.setdefault("engine_prompt_eq", 0)
        c["engine_prompt_eq"] += q.ids == E.build_prompt(etok, pos, "spm", 2000, 512, True, max_prefix=1024, heal=True)
        c["positions"] += 1
print(json.dumps(c))
