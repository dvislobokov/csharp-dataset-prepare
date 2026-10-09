# Tokenizer study (C#)

Script: `scripts/tokenizer_bench.py` (run on the server, 2026-10-09). Own byte-level BPE tokenizers were trained on the
**train** split of the `corpus` config (400.1 MB, 59,615 files) and all tokenizers were evaluated
on **held-out repositories** (validation/test splits: 40.4 MB, 3,585 files) plus
20,000 real `flc-prompt/v2` prompts from validation/test. Our special tokens (`<|eol|>`, `<|sem|>`, ...) were added
as single tokens to every tokenizer. Raw numbers: `docs/tokenizer_bench.json`.

| Tokenizer | Vocab | Chars/token (held-out code) | Prompt tokens mean / p95 / p99 | Prompts ≤ 1024 | Prompts ≤ 2048 | Completion tokens | Embedding params d=512 / d=768 |
|---|---|---|---|---|---|---|---|
| own-bpe-16k | 16,000 | 4.62 | 924.4 / 1592 / 2208 | 53% | 98.7% | 10.45 | 8.2M / 12.3M |
| own-bpe-24k | 24,000 | 4.787 | 887.4 / 1529 / 2081 | 57% | 98.9% | 10.08 | 12.3M / 18.4M |
| own-bpe-32k | 32,000 | 4.866 | 868.6 / 1497 / 2024 | 59% | 99.0% | 9.86 | 16.4M / 24.6M |
| starcoder2 | 49,165 | 4.496 | 892.6 / 1510 / 1907 | 55% | 99.3% | 10.33 | 25.2M / 37.8M |
| deepseek-coder | 32,036 | 3.696 | 1075.3 / 1770 / 2134 | 41% | 98.7% | 11.62 | 16.4M / 24.6M |
| qwen2.5-coder | 151,675 | 4.819 | 824.9 / 1382 / 1739 | 63% | 99.6% | 9.61 | 77.7M / 116.5M |
| gpt2 | 50,270 | 2.297 | 1540.7 / 2716 / 3154 | 32% | 65.4% | 11.59 | 25.7M / 38.6M |

All tokenizers round-trip held-out code exactly (0 failures of 2,000 files).

Pre-tokenisation of the own BPE: letters with one optional leading non-letter, single digits, punctuation runs, newline
runs, whitespace runs kept whole (indentation = few tokens); byte-level, so any UTF-8 is encodable.

## Conclusions

* Own 16k already compresses held-out C# better than StarCoder2's 49k vocabulary (4.62 vs 4.50 chars/token) with a third of
  the embedding parameters; 16k → 32k adds only ~5% compression but doubles embeddings.
* Qwen2.5-Coder compresses slightly better but its embeddings alone exceed a 50M model; GPT-2 is unsuitable for code.
* Recommendation: 50M model (d≈512) → own 16k (8.2M embedding params); 100M model (d≈768) → own 24k (18.4M).
* With the current render budgets (4,000 code + 1,500 semantic chars) ~99% of prompts fit a 2,048-token window but only
  53–59% fit 1,024; for a 1K window lower `--max-code-chars` to ~1,700.
* A joint C#+Go model needs the tokenizer retrained on the mixed corpus.
* Carets often fall inside a token (`.Get`, ` x`, partial identifiers). For FLC fine-tuning tokenize prompt and completion
  separately (as at inference) so the model learns the caret boundaries; token healing can be added at inference.
