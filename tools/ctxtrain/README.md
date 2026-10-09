# Context experiment (flc-context/v1.1)

Does the project profile / per-caret facts block (docs/CONTEXT_SPEC-RU.md) improve the plugin's line completion?
Quick, cheap version: fine-tune the **shipped** 50M model (`.cml`, no pre-training) on caret documents with context (A)
and on the same documents without it (B, control); evaluate on the clean `eval-fresh` set.

| File | Role |
|---|---|
| `flcctx.py` | prompt assembly of v1.1 (insertion point, budgets, token-budgeted facts/profile); engine functions copied |
| `cml_load.py` | `.cml` → PyTorch `CodeLM` (exact int8 dequantisation) |
| `make_docs.py` | caret documents (train shards ctx/noctx) and evaluation positions (none/deps/full prompts) |
| `finetune.py` | fine-tune from a `.cml`, loss only on the completion; checkpoint in the engine's format |
| `eval_ctx.py` | engine greedy decoder (healing, stop rule, repetition guard); exact / shown / precision, per kind, paired bootstrap |
| `gpu_run.sh` | the whole experiment on a 2-GPU machine |
| `tests/` | parity checks (below) |

## Checks done on CPU (2026-10-09)

- `tests/check_parity.py`, 1 000 random positions of real files: healing boundary 3 000/3 000 and stable tail 3 000/3 000
  identical to `eval_inline.py`; the insertion split never changes the line's tokens (1 000/1 000, 240 with a blank line
  above); the no-context prompt equals the engine's SPM prompt byte for byte (1 000/1 000).
- `tests/check_model_parity.py`: the `.cml` loaded into PyTorch + this prompt + the engine decoder vs `ml-core` on 300
  positions: 265 identical completions (88 %); the rest diverge late on near-ties (ml-core uses int8 activations).
- Generator: 3 000 documents in 25 s (facts in 64 %, profile in 75 %, 1 split fallback); one document decoded by hand.
- `finetune.py` on CPU, 30 steps: completion loss 1.11 → 0.64.
- `eval_ctx.py` on CPU, 120 eval-fresh positions: runs end to end; the shipped model with a facts block drops
  (34 % → 16 %: it never saw the reserved tokens) — fine-tuning is required before comparing.

## Data preparation (CPU server)

```bash
python -I make_docs.py --engine <idea-ml-completion> --vocab <engine>/models/cs-16384.bpe --split train --docs 300000 \
    --per-repo 40 --deps-dir /srv/flc/deps/csharp --corpus-dir <corpus parquet dir> --out <out>/train --workers 48
python -I make_docs.py ... --prefix eval-fresh/ --split all --eval --docs 12000 --per-repo 60 \
    --deps-dir /srv/flc/deps/csharp-fresh --corpus-dir <eval-fresh corpus> \
    --exclude <eval-fresh/decontam/near_duplicates.jsonl> --out <out>/eval
```
Semantic facts come from `data/semantic` with `semantic-fix/` overrides (C# extractor fixes, `scripts/semantic_redo.py`).
