# Plugin baseline: current .cml models on CPU (ml-core engine)

Measured 2026-10-09 with the plugin's own inference engine (`NnCompletion.complete`, default `Options`: SPM, healing WORD_EOL, ctx 2000, maxPrefix 1024, suffixTokens 512, maxNew 48, repetition guard) from `github.com/dvislobokov/idea-ml-completion` @ `ae6c50c` (clone, not modified upstream). Harness: `tools/plugin-baseline/` (PluginBaseline.kt test-scope main + python). Raw numbers: `PLUGIN_BASELINE.json`. Models: `cs-nn-31m-e2-lr2e3`, `cs-nn-50m-e3-lr2e3`, `go-nn-31m-e2`, `go-nn-50m-e3-lr2e3`.

## Setup and caveats

- Data: our HF datasets (`dvislobokov/csharp-ml-complation`, `go-ml-complation`), **test split**, 2000 (C#) / 2001 (Go) positions per language, seed 20261009, stratified by `caret_kind` (floor 60 per kind + remainder proportional to the kind's size; so rare kinds are over-represented vs the natural distribution). Empty/whitespace-only targets excluded. `before`/`after` rebuilt from the `corpus` full file: `before = content[:caret_byte]`, `after = content[caret_byte+len(target):]` (the target is erased, the rest of the line after it is empty or trailing whitespace); `content[caret:caret+len(target)] == target_text` verified for every position.
- Mismatches: C# 949/2000 sampled positions are in files with a UTF-8 BOM: `caret_byte_offset` counts the BOM (3 bytes) but corpus `content` has none, so the offset is shifted by -3 there (verified by target match; 0 unresolvable mismatches, 0 sha mismatches, 0 missing files). Go: 0 mismatches.
- **Optimistic caveat**: test repositories (>=35 stars C#, >=100 Go) were very likely in the engine's training corpus (>=20 stars), so the accuracy is an upper-ish bound for these models. `eval-fresh/` (5..19 stars) was NOT available on the HF datasets (404) when the main run finished, so no clean-set numbers.
- Metric: `exact` = generated text (typed remainder stripped, closers trimmed by the engine) == `target_text`, both trailing-whitespace-trimmed. Position-level (one row per caret), not the engine's own `eval_inline` sampling, and our carets are a different mix (many hard `line_start`/`log_message`/`lambda_body`/`after_operator` carets), so absolute exact rates are well below the 50 % (C#) / 64 % (Go) in the engine docs. Do not compare to those directly.
- Engine quirk found: when the caret is right after a typed space/tab that is not line indentation (`throw |`, `as |`, `in |`, `new |`: 409/2000 C# and similar in Go), the engine does not heal the space and the model emits ` exception...` with a leading space (double space on insertion). Strict `exact` counts this as wrong; `exact_lenient` additionally accepts the text with its leading spaces stripped when `before` ends with a space/tab. Both are reported; whether the plugin strips the duplicate space is not visible from the engine repo, so treat lenient as the plugin's likely upper bound.
- Shown/precision: `shown` = confProd >= gate and not punct-only (`);`, `}`) and not repetition-stopped and text non-empty (engine `show` semantics, computed offline from per-position confProd). `plugin policy` = gate 0.5 when `before` ends with `.`/`->`/`::`, else 0.7. Shown rate is over all sampled positions; precision = exact among shown.

## Accuracy (strict exact / lenient exact)

| model | n | exact@all strict | exact@all lenient | gate | shown | precision strict | precision lenient |
|---|---|---|---|---|---|---|---|
| cs-nn-31m-e2-lr2e3 | 2000 | 24.9% | 28.6% | 0.5 | 19.6% | 75.4% | 81.1% |
| cs-nn-31m-e2-lr2e3 | 2000 | 24.9% | 28.6% | 0.7 | 10.2% | 87.7% | 91.2% |
| cs-nn-31m-e2-lr2e3 | 2000 | 24.9% | 28.6% | 0.8 | 6.4% | 89.8% | 93.0% |
| cs-nn-31m-e2-lr2e3 | 2000 | 24.9% | 28.6% | plugin policy | 13.0% | 84.6% | 87.3% |
| cs-nn-50m-e3-lr2e3 | 2000 | 27.7% | 31.8% | 0.5 | 20.2% | 75.0% | 82.2% |
| cs-nn-50m-e3-lr2e3 | 2000 | 27.7% | 31.8% | 0.7 | 10.4% | 84.2% | 89.5% |
| cs-nn-50m-e3-lr2e3 | 2000 | 27.7% | 31.8% | 0.8 | 7.3% | 87.0% | 91.8% |
| cs-nn-50m-e3-lr2e3 | 2000 | 27.7% | 31.8% | plugin policy | 13.0% | 81.9% | 86.2% |
| go-nn-31m-e2 | 2001 | 28.8% | 32.4% | 0.5 | 24.8% | 78.0% | 84.3% |
| go-nn-31m-e2 | 2001 | 28.8% | 32.4% | 0.7 | 15.4% | 83.8% | 89.9% |
| go-nn-31m-e2 | 2001 | 28.8% | 32.4% | 0.8 | 10.8% | 87.0% | 93.5% |
| go-nn-31m-e2 | 2001 | 28.8% | 32.4% | plugin policy | 17.5% | 82.9% | 88.3% |
| go-nn-50m-e3-lr2e3 | 2001 | 29.8% | 34.7% | 0.5 | 25.8% | 77.7% | 84.7% |
| go-nn-50m-e3-lr2e3 | 2001 | 29.8% | 34.7% | 0.7 | 15.9% | 83.6% | 89.9% |
| go-nn-50m-e3-lr2e3 | 2001 | 29.8% | 34.7% | 0.8 | 11.2% | 87.9% | 92.9% |
| go-nn-50m-e3-lr2e3 | 2001 | 29.8% | 34.7% | plugin policy | 18.0% | 84.2% | 89.7% |

`exact@all` is the unconditional rest-of-line exact-match rate over all sampled positions (what a model must beat when it always suggests).

## Break-down by caret_kind (lenient exact; strict in JSON)

### C#

| caret_kind | n | cs-nn-31m-e2-lr2e3 exact@all / shown@0.7 / prec@0.7 | cs-nn-50m-e3-lr2e3 exact@all / shown@0.7 / prec@0.7 |
|---|---|---|---|
| after_keyword | 113 | 15.9% / 2.7% / 66.7% | 15.0% / 1.8% / 50.0% |
| after_operator | 155 | 18.1% / 1.9% / 66.7% | 24.5% / 4.5% / 100.0% |
| argument_list | 215 | 33.5% / 11.6% / 100.0% | 34.9% / 12.6% / 92.6% |
| control_flow | 89 | 19.1% / 6.7% / 83.3% | 20.2% / 4.5% / 75.0% |
| identifier_partial | 314 | 51.0% / 23.9% / 94.7% | 52.2% / 21.3% / 94.0% |
| lambda_body | 70 | 14.3% / 1.4% / 100.0% | 15.7% / 0.0% / - |
| line_start | 477 | 16.8% / 4.8% / 78.3% | 22.6% / 5.9% / 78.6% |
| linq | 86 | 25.6% / 10.5% / 77.8% | 32.6% / 10.5% / 88.9% |
| log_message | 62 | 4.8% / 0.0% / - | 3.2% / 0.0% / - |
| member_access | 297 | 38.0% / 11.8% / 91.4% | 40.4% / 13.5% / 85.0% |
| token_boundary | 122 | 40.2% / 19.7% / 95.8% | 45.1% / 20.5% / 96.0% |

### Go

| caret_kind | n | go-nn-31m-e2 exact@all / shown@0.7 / prec@0.7 | go-nn-50m-e3-lr2e3 exact@all / shown@0.7 / prec@0.7 |
|---|---|---|---|
| after_keyword | 122 | 10.7% / 0.8% / 100.0% | 17.2% / 0.0% / - |
| after_operator | 133 | 6.8% / 0.8% / 0.0% | 6.0% / 0.8% / 100.0% |
| argument_list | 230 | 30.4% / 14.8% / 88.2% | 31.3% / 15.7% / 91.7% |
| composite_literal | 115 | 39.1% / 20.9% / 87.5% | 40.9% / 20.9% / 87.5% |
| control_flow | 89 | 5.6% / 0.0% / - | 5.6% / 0.0% / - |
| error_handling | 77 | 24.7% / 11.7% / 88.9% | 26.0% / 10.4% / 87.5% |
| func_literal | 65 | 9.2% / 4.6% / 100.0% | 24.6% / 4.6% / 100.0% |
| identifier_partial | 245 | 57.6% / 32.7% / 96.2% | 58.4% / 32.7% / 96.2% |
| line_start | 396 | 28.3% / 8.6% / 94.1% | 31.8% / 9.8% / 89.7% |
| log_message | 100 | 13.0% / 3.0% / 33.3% | 12.0% / 4.0% / 50.0% |
| member_access | 316 | 49.4% / 28.5% / 86.7% | 49.7% / 30.1% / 86.3% |
| token_boundary | 113 | 52.2% / 25.7% / 89.7% | 60.2% / 24.8% / 89.3% |

Small per-kind n (60-300) means +-3-8 pp noise; per-kind precision on few shown items is very noisy.

## Latency (this CPU, native q8 kernels, 8 threads)

Server: 128-vCPU AMD EPYC 9754, shared with other jobs (load average 7-15 during the runs), JVM pinned with `taskset -c 0-7`, `NnModel(nThreads=8)`, kernels `native-avx512-vnni-q8`, JDK 21. **This is a big server, not a laptop**: AVX-512 VNNI, no thermal limits; laptop numbers will differ (the engine docs quote ~150 ms for a 1500-token cold prefill at 8 threads on their reference machine; we measured ~235-250 ms for the 31M model on this shared host, cause not investigated).\n
Method: 25 discarded warm-up completions (JIT), then 40 positions per model from our test cases with real prompts of 1300-1700 tokens (mean ~1540). **Cold** = fresh session (no KV reuse), full `complete()` incl. healing, tokenization, prefill and greedy decode of the line. **Prefill only** = the same prompt through `session.prefill` cold. **Warm keystroke** = the position's cold completion done first, then the user 'types' 1 (resp. 2) further characters of the target: `complete()` again on the same session (prefix reuse).

| model | prompt tok (mean) | gen tok (mean) | cold complete p50 / p95 ms | prefill only p50 ms | decode ms/token (est.) | warm keystroke p50 / p95 ms (mean) |
|---|---|---|---|---|---|---|
| cs-nn-31m-e2-lr2e3 | 1540 | 8.4 | 260 / 320 | 250 | 1.37 | 13.8 / 57 (18.0) |
| cs-nn-50m-e3-lr2e3 | 1540 | 7.3 | 413 / 496 | 400 | 2.17 | 17.6 / 97 (27.9) |
| go-nn-31m-e2 | 1546 | 7.5 | 263 / 301 | 253 | 1.50 | 11.2 / 40 (14.0) |
| go-nn-50m-e3-lr2e3 | 1546 | 8.5 | 426 / 486 | 405 | 2.25 | 17.2 / 77 (23.1) |

Warm p95 is dominated by keystrokes where the healed boundary or the 256-token stable-prefix cut moves and more of the prompt is recomputed; the median is the typical case. (Warm keystroke #2 is the same, see JSON.)

Existing `NnBench` (random 1500-token prompt, 20 gen, `--reuse 8` new prompt tokens, real weights, 20 measured runs, 8 threads):

| model | params | prefill ms | decode ms/token | line ms (cold) | line ms (8 new tokens reused) | RSS MB | file MB |
|---|---|---|---|---|---|---|---|
| cs-nn-31m-e2-lr2e3 | 30.9M | 234.8 | 1.35 | 260.5 | 29.0 | 335 | 31.2 |
| cs-nn-50m-e3-lr2e3 | 49.8M | 378.5 | 2.20 | 419.7 | 48.7 | 383 | 50.1 |
| go-nn-31m-e2 | 30.9M | 235.6 | 1.38 | 261.8 | 30.7 | 335 | 31.2 |
| go-nn-50m-e3-lr2e3 | 49.8M | 376.3 | 2.28 | 419.5 | 49.5 | 383 | 50.1 |

First call (cold JVM/JIT) is 1.6-1.8x slower than steady state (`first_line_ms` in JSON).

## What a new model has to beat

- Accuracy: on this test mix, 31M/50M give 25-31 % (C#) and 29-30 % (Go) unconditional strict rest-of-line exact (lenient 29-32 % / 32-35 %); at the plugin gate (0.7, 0.5 after a dot) they show 13 % (C#) / 17.5-18 % (Go) of positions at ~82-85 % (strict) / ~87-90 % (lenient) precision. Optimistic (train/test overlap).
- Latency: cold ~1500-token prompt 260 ms (31M) / 410 ms (50M) at 8 native threads; warm keystroke median 11-18 ms (p95 40-97 ms).
- Memory: ~335-385 MB RSS for 8-thread JVM + mmap'd model.

## Reproduce

`tools/plugin-baseline/`: `dl.py` (HF test shards) -> `mkcases.py <cs|go> <dir> cases.tsv 2000` -> `PluginBaseline.kt` copied to `ml-core/src/test/kotlin/io/github/completionml/core/nn/` in a clone, `./gradlew :ml-core:testClasses`, classpath via `:ml-core:printBenchClasspath`; `PluginBaseline acc|lat ...`; `analyze.py`; `make_report.py`. Server work dir: `/srv/mlbench` (raw per-position TSVs in `out/`).
