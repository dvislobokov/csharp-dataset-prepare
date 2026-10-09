#!/usr/bin/env python3
"""
Tokenizer study for the C# completion model.

Trains byte-level BPE tokenizers on the TRAIN split of the corpus (whole files) and compares them with existing code
tokenizers on HELD-OUT repositories (validation/test splits): compression, prompt lengths of real flc-prompt/v2 records,
completion lengths, embedding share of a 50M/100M model, and example segmentations.

  python -I scripts/tokenizer_bench.py --out /srv/flc/tokenizers --train-mb 400
"""
from __future__ import annotations

import argparse
import json
import os
import random
import statistics
import time

SPECIAL = ["<|endoftext|>", "<|pad|>", "<|cs|>", "<|go|>", "<|path|>", "<|sem|>", "<|code|>", "<|complete|>", "<|eol|>",
           "<|end_completion|>", "<|fim_prefix|>", "<|fim_middle|>", "<|fim_suffix|>", "<|eos|>"]
# GPT-4-like pre-tokenisation, adapted to code: letters (with an optional leading non-letter), single digits, punctuation runs,
# newline runs, and whitespace runs kept whole so indentation becomes a few tokens.
PATTERN = r"""[^\r\n\p{L}\p{N}]?\p{L}+|\p{N}| ?[^\s\p{L}\p{N}]+[\r\n]*|\s*[\r\n]+|\s+(?!\S)|\s+"""
EXTERNAL = {
    "starcoder2": "bigcode/starcoder2-3b",
    "deepseek-coder": "deepseek-ai/deepseek-coder-1.3b-base",
    "qwen2.5-coder": "Qwen/Qwen2.5-Coder-0.5B",
    "gpt2": "openai-community/gpt2",
}
EXAMPLES = [
    "        var order = await _orderRepository.GetByIdAsync(request.OrderId, cancellationToken);",
    "            logger.LogInformation(\"Order {OrderId} was paid\", order.Id);",
    "    public IReadOnlyList<OrderItemDto> Items { get; init; } = [];",
]


def hf_files(api, repo, prefix):
    return sorted(f for f in api.list_repo_files(repo, repo_type="dataset") if f.startswith(prefix) and f.endswith(".parquet"))


def read_texts(fs_files, column, limit_bytes, seed, api, repo, token, cache):
    """Stream text from parquet shards (shuffled deterministically) until limit_bytes of UTF-8 are collected."""
    import pyarrow.parquet as pq
    from huggingface_hub import hf_hub_download
    rnd = random.Random(seed)
    files = list(fs_files)
    rnd.shuffle(files)
    out, total = [], 0
    for f in files:
        p = hf_hub_download(repo, f, repo_type="dataset", token=token, cache_dir=cache)
        rows = pq.read_table(p, columns=[column]).column(0).to_pylist()
        rnd.shuffle(rows)
        for t in rows:
            if not t:
                continue
            out.append(t)
            total += len(t.encode("utf-8"))
            if total >= limit_bytes:
                return out, total
    return out, total


def train_bpe(texts, vocab, threads):
    from tokenizers import Regex, Tokenizer, decoders, models, pre_tokenizers, trainers
    os.environ["RAYON_NUM_THREADS"] = str(threads)
    tok = Tokenizer(models.BPE(byte_fallback=False))
    tok.pre_tokenizer = pre_tokenizers.Sequence([
        pre_tokenizers.Split(Regex(PATTERN), behavior="isolated"),
        pre_tokenizers.ByteLevel(add_prefix_space=False, use_regex=False),
    ])
    tok.decoder = decoders.ByteLevel()
    trainer = trainers.BpeTrainer(vocab_size=vocab, min_frequency=2, special_tokens=SPECIAL,
                                  initial_alphabet=pre_tokenizers.ByteLevel.alphabet(), show_progress=False)
    tok.train_from_iterator(texts, trainer=trainer, length=len(texts) if hasattr(texts, "__len__") else None)
    return tok


def load_external(name, repo, cache):
    from huggingface_hub import hf_hub_download
    from tokenizers import Tokenizer
    p = hf_hub_download(repo, "tokenizer.json", cache_dir=cache)
    tok = Tokenizer.from_file(p)
    tok.add_special_tokens(SPECIAL)  # our markers must be single tokens for prompt-length comparison
    return tok


def pct(values, q):
    v = sorted(values)
    return v[min(len(v) - 1, int(q * len(v)))] if v else 0


def evaluate(name, tok, heldout, prompts, completions):
    t0 = time.time()
    enc = tok.encode_batch(heldout, add_special_tokens=False)
    secs = time.time() - t0
    ntok = sum(len(e.ids) for e in enc)
    nchar = sum(len(t) for t in heldout)
    nbyte = sum(len(t.encode("utf-8")) for t in heldout)
    # exact round trip on held-out code (byte-level tokenizers must be lossless)
    bad = sum(1 for t, e in zip(heldout[:2000], enc[:2000]) if tok.decode(e.ids, skip_special_tokens=False) != t)
    plen = [len(e.ids) for e in tok.encode_batch(prompts, add_special_tokens=False)]
    clen = [len(e.ids) for e in tok.encode_batch(completions, add_special_tokens=False)]
    vocab = tok.get_vocab_size()
    return {
        "tokenizer": name, "vocab": vocab,
        "chars_per_token": round(nchar / ntok, 3), "bytes_per_token": round(nbyte / ntok, 3),
        "roundtrip_failures_of_2000": bad, "encode_mb_per_s": round(nbyte / 2**20 / max(secs, 1e-9), 1),
        "prompt_tokens_mean": round(statistics.mean(plen), 1), "prompt_tokens_p95": pct(plen, 0.95), "prompt_tokens_p99": pct(plen, 0.99),
        "prompts_fit_1024": round(sum(1 for x in plen if x <= 1024) / len(plen), 4),
        "prompts_fit_2048": round(sum(1 for x in plen if x <= 2048) / len(plen), 4),
        "completion_tokens_mean": round(statistics.mean(clen), 2), "completion_tokens_p95": pct(clen, 0.95),
        # tied input/output embeddings; widths typical for 50M / 100M decoder-only models
        "embedding_params_M_d512": round(vocab * 512 / 1e6, 1), "embedding_params_M_d768": round(vocab * 768 / 1e6, 1),
        "examples": [[tok.decode([i]) for i in tok.encode(x, add_special_tokens=False).ids] for x in EXAMPLES],
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--repo", default="dvislobokov/csharp-ml-complation")
    ap.add_argument("--token-file", default="/srv/flc/secrets/HF_TOKEN")
    ap.add_argument("--out", required=True)
    ap.add_argument("--train-mb", type=int, default=400)
    ap.add_argument("--heldout-mb", type=int, default=40)
    ap.add_argument("--prompts", type=int, default=20000)
    ap.add_argument("--vocabs", default="16000,24000,32000")
    ap.add_argument("--threads", type=int, default=32)
    ap.add_argument("--seed", type=int, default=20261009)
    args = ap.parse_args()
    from huggingface_hub import HfApi
    token = open(args.token_file).read().strip()
    api = HfApi(token=token)
    cache = os.path.join(args.out, "hf-cache")
    os.makedirs(args.out, exist_ok=True)
    t = time.time()
    train, train_bytes = read_texts(hf_files(api, args.repo, "data/corpus/train-"), "content", args.train_mb * 2**20, args.seed, api, args.repo, token, cache)
    held_files = hf_files(api, args.repo, "data/corpus/validation-") + hf_files(api, args.repo, "data/corpus/test-")
    heldout, held_bytes = read_texts(held_files, "content", args.heldout_mb * 2**20, args.seed, api, args.repo, token, cache)
    pfiles = hf_files(api, args.repo, "data/prompts/validation-") + hf_files(api, args.repo, "data/prompts/test-")
    import pyarrow.parquet as pq
    from huggingface_hub import hf_hub_download
    prompts, completions = [], []
    rnd = random.Random(args.seed)
    for f in pfiles:
        tbl = pq.read_table(hf_hub_download(args.repo, f, repo_type="dataset", token=token, cache_dir=cache), columns=["prompt", "completion"])
        prompts += tbl.column(0).to_pylist()
        completions += tbl.column(1).to_pylist()
    idx = rnd.sample(range(len(prompts)), min(args.prompts, len(prompts)))
    prompts, completions = [prompts[i] for i in idx], [completions[i] for i in idx]
    data_info = {"train_files": len(train), "train_mb": round(train_bytes / 2**20, 1), "heldout_files": len(heldout),
                 "heldout_mb": round(held_bytes / 2**20, 1), "prompts": len(prompts), "load_s": round(time.time() - t, 1)}
    print(json.dumps(data_info), flush=True)

    results = []
    for v in [int(x) for x in args.vocabs.split(",")]:
        t = time.time()
        tok = train_bpe(train, v, args.threads)
        train_s = round(time.time() - t, 1)
        path = os.path.join(args.out, f"csharp-bpe-{v // 1000}k.json")
        tok.save(path)
        r = evaluate(f"own-bpe-{v // 1000}k", tok, heldout, prompts, completions)
        r["train_s"] = train_s
        r["file"] = path
        results.append(r)
        print(json.dumps({k: r[k] for k in r if k != "examples"}), flush=True)
    for name, repo in EXTERNAL.items():
        try:
            tok = load_external(name, repo, cache)
        except Exception as e:  # noqa: BLE001 - a gated/missing tokenizer is reported, not fatal
            results.append({"tokenizer": name, "error": f"{type(e).__name__}: {str(e)[:200]}"})
            print(json.dumps(results[-1]), flush=True)
            continue
        r = evaluate(name, tok, heldout, prompts, completions)
        results.append(r)
        print(json.dumps({k: r[k] for k in r if k != "examples"}), flush=True)
    json.dump({"data": data_info, "results": results}, open(os.path.join(args.out, "tokenizer_bench.json"), "w"), indent=2, ensure_ascii=False)


if __name__ == "__main__":
    main()
