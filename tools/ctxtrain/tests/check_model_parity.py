"""PyTorch model loaded from .cml + flcctx prompt + engine greedy decoder vs ml-core (NnCompletion) outputs."""
import base64, json, os, sys, time
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
import flcctx as F
eng, vocab, cml, cases, results, n = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5], int(sys.argv[6])
F.engine_paths(eng)
import torch, eval_inline as E
from cml_load import load_model
torch.set_num_threads(48)
tok, etok = F.Tok(vocab), E.Tokenizer(vocab)
model, meta = load_model(cml)
model.eval()
gen = E.Generator(model, etok, "cpu")
d = lambda s: base64.b64decode(s)
rows = [l.rstrip("\n").split("\t") for l in open(cases)][:n]
unesc = lambda s: s.replace("\\n", "\n").replace("\\r", "\r").replace("\\t", "\t").replace("\\\\", "\\")
ref = {}
for l in list(open(results))[1:]:
    p = l.rstrip("\n").split("\t")
    ref[p[0]] = (unesc(p[14]) if len(p) > 14 else None, float(p[3]) if len(p) > 3 and p[2] != "ERR" else None)
CL = set(b" \t)]}>;,\"'`")
def trim_closers(g, after_line):
    for k in range(min(len(g), len(after_line)), 0, -1):
        tail, head = g[len(g) - k:], after_line[:k]
        if tail == head and all(c in CL for c in head):
            return g[:len(g) - k]
    return g
same = diff = 0; examples = []; t0 = time.time()
B = 16
for k in range(0, len(rows), B):
    part = rows[k:k + B]
    prompts, typed, after_lines = [], [], []
    for r in part:
        path, before, after = d(r[3]).decode(), d(r[4]), d(r[5])
        text, caret = before + after, len(before)
        q = F.build(tok, text, caret, path, reserve_context=False)
        prompts.append(q.ids); typed.append(q.typed)
        eol = after.find(b"\n"); after_lines.append(after[:eol if eol >= 0 else len(after)])
    with torch.no_grad():
        res = gen.generate(prompts, 48, constraints=typed, rep_guard=True)
    for r, ty, al, (ids, probs, stop_p, kind, _, _) in zip(part, typed, after_lines, res):
        g = etok.decode(ids)
        g = g[len(ty):] if g.startswith(ty) else g
        g = trim_closers(g, al).decode("utf-8", "replace")
        want = ref.get(r[0], (None, None))[0]
        if want is None:
            continue
        if g == want:
            same += 1
        else:
            diff += 1
            if len(examples) < 6:
                examples.append({"id": r[0], "kind": r[1], "torch": g, "mlcore": want})
print(json.dumps({"compared": same + diff, "identical": same, "different": diff, "seconds": round(time.time() - t0)}))
for e in examples:
    print(json.dumps(e, ensure_ascii=False))
