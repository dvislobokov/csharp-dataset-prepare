#!/usr/bin/env python3
# Copied next to the idea-ml-completion tokenizer sources (cmlbpe.py, train_bpe.py, manifest.py from tools/nn/tokenizer)
# and run on the server; see docs/TOKENIZER.md ("Plugin engine tokenizer vs HF tokenizers").
"""Compare Go tokenizers on held-out repositories of our corpus (validation+test splits).

A: engine's go-16384.bpe (plugin), B: engine-format .bpe trained by the engine's own train_bpe.train() on OUR train split,
C/D: HF tokenizers trained on the same sample. Metrics: bytes/token and chars/token on held-out files, completion (rest-of-line) tokens on real
samples, exact round trip.
"""
import collections, glob, json, multiprocessing as mp, os, random, sys, time
import pyarrow.parquet as pq

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cmlbpe
from train_bpe import train

SRC = "/srv/flc/tokcmp-go/src/data/corpus"
OUT = os.path.dirname(os.path.abspath(__file__))


def log(**kw):
    print(json.dumps(kw, ensure_ascii=False), flush=True)


def water_fill(budget, seed=1, max_file=256 * 1024):
    """Engine's sampling: every repo contributes at most `cap` bytes, total ~budget."""
    by_repo = collections.defaultdict(list)
    for f in sorted(glob.glob(f"{SRC}/train-*.parquet")):
        t = pq.read_table(f, columns=["repository_id", "content"])
        for r, c in zip(t.column(0).to_pylist(), t.column(1).to_pylist()):
            b = c.encode("utf-8")
            if len(b) <= max_file:
                by_repo[r].append(b)
    tot = {r: sum(len(x) for x in v) for r, v in by_repo.items()}
    lo, hi = 0, max(tot.values())
    while lo < hi:
        mid = (lo + hi) // 2
        if sum(min(t, mid) for t in tot.values()) < budget: lo = mid + 1
        else: hi = mid
    rnd = random.Random(seed); out = []
    for r in sorted(by_repo):
        fs = by_repo[r]; rnd.shuffle(fs); used = 0
        for b in fs:
            if used >= lo: break
            out.append(b); used += len(b)
    log(event="sample", repos=len(by_repo), cap_mb=round(lo / 1e6, 2), files=len(out), gb=round(sum(map(len, out)) / 1e9, 3))
    return out


def count(batch):
    c = collections.Counter()
    for b in batch: c.update(cmlbpe.pretokenize(b))
    return c


def train_engine_bpe(path, size=16384, gb=1.5):
    files = water_fill(int(gb * 1e9))
    sys.path.insert(0, "/srv/flc/app/scripts")
    from tokenizer_bench import train_bpe
    texts = [b.decode("utf-8") for b in files]
    for v in (16000, 24000):
        hp = f"{OUT}/go-hf-{v // 1000}k.json"
        if not os.path.exists(hp):
            t1 = time.time(); train_bpe(texts, v, 48).save(hp); log(event="hf_trained", path=hp, seconds=round(time.time() - t1))
    del texts
    t0 = time.time(); wc = collections.Counter()
    with mp.Pool(48) as pool:
        for c in pool.imap_unordered(count, [files[i:i + 200] for i in range(0, len(files), 200)]):
            wc.update(c)
    kept = sorted(((w, c) for w, c in wc.items() if c >= 2), key=lambda x: (-x[1], x[0]))
    log(event="counted", distinct=len(wc), kept=len(kept), seconds=round(time.time() - t0))
    del wc, files
    merges = train(kept, size - 256 - len(cmlbpe.SPECIALS), log_every=4000)
    cmlbpe.Vocab(merges).save(path)
    log(event="trained", path=path, seconds=round(time.time() - t0))


def heldout(max_bytes=150_000_000, seed=7):
    files = []
    for sp in ("validation", "test"):
        for f in sorted(glob.glob(f"{SRC}/{sp}-*.parquet")):
            files += pq.read_table(f, columns=["content"]).column(0).to_pylist()
    random.Random(seed).shuffle(files)
    out, n = [], 0
    for c in files:
        out.append(c); n += len(c.encode("utf-8"))
        if n >= max_bytes: break
    return out


_enc = None
def _init(kind, path):
    global _enc
    if kind == "bpe":
        e = cmlbpe.Encoder(cmlbpe.Vocab.load(path))
        _enc = (lambda s: e.encode_bytes(s.encode("utf-8")), lambda ids: e.decode_bytes(ids).decode("utf-8"))
    else:
        from tokenizers import Tokenizer
        t = Tokenizer.from_file(path)
        _enc = (lambda s: t.encode(s, add_special_tokens=False).ids, lambda ids: t.decode(ids, skip_special_tokens=False))


def _work(batch):
    enc, dec = _enc
    nt = bad = 0; lens = []
    for i, s in enumerate(batch):
        ids = enc(s); nt += len(ids)
        if i % 10 == 0 and dec(ids) != s: bad += 1
    return nt, bad


def _work_lines(batch):
    enc, _ = _enc
    return [len(enc(s)) for s in batch]


def main():
    newbpe = f"{OUT}/go-ours-16384.bpe"
    if not os.path.exists(newbpe):
        train_engine_bpe(newbpe)
    texts = heldout()
    nbytes = sum(len(s.encode("utf-8")) for s in texts); nchars = sum(len(s) for s in texts)
    tgt = []
    for f in sorted(glob.glob("/srv/flc/tokcmp-go/samples-test-*.parquet")):
        tgt += pq.read_table(f, columns=["target_text"]).column(0).to_pylist()
    random.Random(3).shuffle(tgt); tgt = tgt[:100000]
    log(event="heldout", files=len(texts), mb=round(nbytes / 2**20, 1), targets=len(tgt))
    cands = [("A engine go-16384.bpe (plugin now)", "bpe", f"{OUT}/go-16384.bpe"),
             ("B engine format, trained on our Go corpus", "bpe", newbpe),
             ("C HF 16k, trained on our Go corpus", "hf", f"{OUT}/go-hf-16k.json"),
             ("D HF 24k, trained on our Go corpus", "hf", f"{OUT}/go-hf-24k.json")]
    res = []
    for name, kind, path in cands:
        t0 = time.time()
        with mp.Pool(48, _init, (kind, path)) as pool:
            r = pool.map(_work, [texts[i:i + 100] for i in range(0, len(texts), 100)])
            tl = sum(pool.map(_work_lines, [tgt[i:i + 2000] for i in range(0, len(tgt), 2000)]), [])
        _init(kind, path)
        vocab = cmlbpe.Vocab.load(path).vocab_size if kind == "bpe" else json.load(open(path))["model"]["vocab"].__len__() + len(json.load(open(path))["added_tokens"])
        nt = sum(x[0] for x in r); bad = sum(x[1] for x in r)
        tl.sort()
        row = dict(tokenizer=name, vocab=vocab, bytes_per_token=round(nbytes / nt, 3), chars_per_token=round(nchars / nt, 3),
                   heldout_tokens_M=round(nt / 1e6, 2), completion_tokens_mean=round(sum(tl) / len(tl), 2),
                   completion_tokens_p95=tl[int(0.95 * len(tl))], roundtrip_failures=bad,
                   emb_params_M_d512=round(vocab * 512 / 1e6, 1), emb_params_M_d640=round(vocab * 640 / 1e6, 1), seconds=round(time.time() - t0))
        res.append(row); log(**row)
    json.dump(res, open(f"{OUT}/compare.json", "w"), indent=2, ensure_ascii=False)


if __name__ == "__main__":
    main()
