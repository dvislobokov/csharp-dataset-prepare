import sys, os, numpy as np
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
import flcctx as F
F.engine_paths(sys.argv[1]); tok = F.Tok(sys.argv[2]); d = sys.argv[3]
t = np.memmap(d + "/ctx.tokens.u16", dtype=np.uint16, mode="r"); o = np.fromfile(d + "/ctx.offsets.u64", dtype=np.uint64); ls = np.fromfile(d + "/ctx.loss_start.u32", dtype=np.uint32)
names = {tok.r0: "<R0>", tok.r1: "<R1>", tok.r2: "<R2>", tok.fim_prefix: "<FP>", tok.fim_suffix: "<FS>", tok.fim_middle: "<FM>", tok.file_sep: "<FILE>", tok.eot: "<EOT>"}
for i in range(len(ls)):
    ids = [int(x) for x in t[o[i]:o[i + 1]]]
    if tok.r1 not in ids or tok.r0 not in ids:
        continue
    def dec(xs):
        out, buf = [], []
        for x in xs:
            if x in names:
                if buf: out.append(tok.decode(buf).decode("utf-8", "replace")); buf = []
                out.append(names[x])
            else:
                buf.append(x)
        if buf: out.append(tok.decode(buf).decode("utf-8", "replace"))
        return "".join(out)
    p = dec(ids[:ls[i]]); fm = p.index("<FM>")
    print("...SUFFIX skipped...", p[fm:fm + 300].replace("\n", "\\n"), "\n...\n", p[-700:])
    print("=== TARGET (loss):", repr(dec(ids[ls[i]:])))
    break
