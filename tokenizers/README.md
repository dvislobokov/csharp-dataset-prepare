# C# tokenizers

Byte-level BPE tokenizers for the C# full-line completion model (a separate model and tokenizer are planned for Go).

| File | Vocab | Chars/token on held-out C# | Prompts ≤ 2048 tokens | Embedding params (tied) d=512 / d=768 | Use for |
|---|---|---|---|---|---|
| `csharp-bpe-16k.json` | 16,000 | 4.62 | 98.7% | 8.2M / 12.3M | ~50M model |
| `csharp-bpe-24k.json` | 24,000 | 4.79 | 98.9% | 12.3M / 18.4M | ~100M model |
| `csharp-bpe-32k.json` | 32,000 | 4.87 | 99.0% | 16.4M / 24.6M | comparison |

Full comparison with StarCoder2, DeepSeek-Coder, Qwen2.5-Coder and GPT-2: `docs/TOKENIZER.md`.

**Training data.** 400 MB (59,615 files) sampled from the **train** split of the `corpus` config of
`dvislobokov/csharp-ml-complation`; evaluation used held-out repositories (validation/test splits) only.
Trained with `scripts/tokenizer_bench.py` (`tokenizers` 0.23, `min_frequency=2`). These are study/first-experiment
tokenizers; the final one should be retrained on the full train corpus with the same settings.

**Design.** Byte-level (any UTF-8 is encodable, lossless round trip). Pre-tokenisation: letters with one optional leading
non-letter, single digits, punctuation runs, newline runs, whitespace runs kept whole (indentation = few tokens).
Special tokens (ids 0–13): `<|endoftext|> <|pad|> <|cs|> <|go|> <|path|> <|sem|> <|code|> <|complete|> <|eol|>
<|end_completion|> <|fim_prefix|> <|fim_middle|> <|fim_suffix|> <|eos|>` — the markers of `flc-prompt/v2`
(`docs/PROMPT_FORMAT.md`) plus FIM and document separators.

```python
from tokenizers import Tokenizer
tok = Tokenizer.from_file("tokenizers/csharp-bpe-16k.json")
ids = tok.encode("var order = await repo.GetByIdAsync(id);").ids

# transformers
from transformers import PreTrainedTokenizerFast
hf_tok = PreTrainedTokenizerFast(tokenizer_file="tokenizers/csharp-bpe-16k.json",
                                 eos_token="<|endoftext|>", pad_token="<|pad|>")
```

For FLC fine-tuning tokenize `prompt` and `completion` separately (exactly as the IDE will at inference) so the model
learns caret boundaries that fall inside tokens (`.Get`, ` x`, partial identifiers).
