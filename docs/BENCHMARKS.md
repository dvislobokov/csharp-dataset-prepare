# Benchmarks — eShop pilot

Raw results: `artifacts/benchmarks/benchmark.json`, generated report `artifacts/benchmarks/benchmark.md`, per-run
`run-manifest.json` under `artifacts/benchmarks/runs/*`. Reproduce:

```bash
flc benchmark --repo data/repos/eShop --config configs/eshop.pilot.json --out artifacts/benchmarks --overwrite \
    --workers 8 --trusted-project-evaluation
```

## Methodology

* Every experiment runs as a **separate child process** of the CLI (Release build): cold JIT, fresh heap, so peak RSS / managed
  memory (sampled every 25 ms) and allocations belong to one run. `validate` runs afterwards as its own process.
* Timings are split: `workspace_load` (MSBuild/adhoc), `discovery_io`, `pipeline_wall` (discovery → syntax → semantic →
  serialization), `writer_serialization`, `finalize_outputs`; worker sums `Σ parse`, `Σ extract`, `Σ semantic`; latency
  percentiles per file (syntax) and per semantic analysis (one sample × one policy).
* Restore is not part of any run (it is a separate, trusted, networked step). Measured manually with `/usr/bin/time`:
  whole-solution `dotnet restore eShop.slnx` fails in 2.2 s (`NETSDK1147`: MAUI workloads); per-project restore of the 25
  non-MAUI projects took **53.6 s** with a cold NuGet cache.
* Sequential baseline (`--workers 1`) precedes parallel runs. Parallelism is bounded (2 × workers files in flight per stage).
* Token counts are not reported: no tokenizer is pinned yet (lines/chars instead).

Environment: AMD Ryzen 7 8845HS (16 logical CPUs), 27.8 GiB RAM, Fedora 42, .NET 10.0.12 runtime, SDK 10.0.401, Roslyn 5.9.0,
eShop `dc7ea499cd356924fb6689b3702964a5869dbae9`, config `eshop-pilot/1`, seed 20261009.

## Results (2026-10-09 run)

| Run | Workers | Pipeline wall s | CPU s | Peak RSS MiB | Alloc MiB | Samples | Samples/s | Source MiB/s | Output MiB | Valid |
|---|---|---|---|---|---|---|---|---|---|---|
| E0 discover | 1 | 0.08 | 0.24 | 63 | 16 | – | – | 8.19 | 1.2 | ok |
| E1 syntax seq (1st) | 1 | 1.88 | 2.53 | 106 | 535 | 14,071 | 7,473 | 0.363 | 57.8 | ok |
| E1 syntax seq (2nd) | 1 | 1.91 | 2.57 | 107 | 536 | 14,071 | 7,373 | 0.358 | 57.8 | ok |
| E1 syntax par | 8 | 0.81 | 2.94 | 133 | 679 | 14,071 | 17,474 | 0.848 | 57.8 | ok |
| E1 syntax par + gzip | 8 | 0.88 | 3.11 | 131 | 671 | 14,071 | 16,043 | 0.778 | **1.7** | ok |
| E2 adhoc (10% subset) | 8 | 22.6 | 64.3 | 385 | 3,986 | 14,071 | 623 | 0.030 | 68.2 | ok |
| E2 msbuild (10%) seq | 1 | 59.3 | 81.4 | 616 | 8,931 | 14,071 | 237 | 0.011 | 67.0 | ok |
| E2 msbuild (10%) par | 8 | 41.5 | 100.7 | 633 | 9,358 | 14,071 | 339 | 0.017 | 67.0 | ok |
| E3 msbuild (100%) par | 8 | 230.2 | 556.9 | 744 | 79,813 | 14,071 | 61 | 0.003 | 150.0 | ok |

Workspace load (not in pipeline wall): MSBuild `eShop.Web.slnf` 3.8–4.2 s (25 projects, 485 documents, 0 diagnostics);
adhoc 0.4 s (29 project groups incl. MAUI and `(no-project)`).

### E0 — corpus

588 candidate `.cs` paths under `src/` and `tests/`; **519 accepted** (49 test files), 715,894 bytes, 21,319 lines.
Skipped: 41 `excluded_path` (obj/bin), 22 `generated_path` (EF migrations/snapshots), 2 `generated_marker`, 4 `exact_duplicate`.
License: MIT, verified from `LICENSE` text and matching the declared value.

### E1 — samples

14,071 samples (train 12,808 / eval 1,263; test code 2,550). By kind: line_start 4,490 · member_access 2,856 ·
identifier_partial 2,730 · argument_list 1,331 · after_operator 821 · token_boundary 660 · after_keyword 542 · linq 300 ·
control_flow 186 · lambda_body 93 · log_message 62. Mean target 33.9 chars. 3,453 boilerplate duplicates dropped
(duplicate fraction 0.197). Negative strata counted: 2,053 in interpolated strings, 1,533 in strings, 633 comment lines,
200 doc-comment lines, 59 raw multi-line strings, 31 inactive `#if` lines, etc. Reconstruction/offset/schema validation:
0 failures on all runs.

### E2/E3 — semantic coverage and cost

| Run | attempted | resolved | partial | syntax_fallback | failed |
|---|---|---|---|---|---|
| E2 adhoc | 1,387 | 1,243 | 144 | 0 | 0 |
| E2 msbuild | 1,387 | 964 | 6 | 417 | 0 |
| E3 msbuild | 14,071 | 9,519 | 64 | 4,488 | 0 |

All `syntax_fallback` reasons are `document_not_in_workspace`: MAUI projects (`ClientApp`, `ClientApp.UnitTests`,
`HybridApp`) are not in `eShop.Web.slnf` and need workloads. Leak violations: **0** in every run and policy.

Per-analysis latency (one sample × one policy): MSBuild sequential p50 22 ms / p95 81 ms / p99 198 ms; 8 workers p50 40 ms /
p95 178 ms. Adhoc p50 20 ms / p95 49 ms.

### E4 — editor_snapshot vs strict_prefix (E3, 9,583 paired samples)

| Metric | editor_snapshot | strict_prefix |
|---|---|---|
| resolved / partial | 9,519 / 64 | 9,433 / 150 |
| mean locals · parameters · this-members | 1.28 · 1.31 · 10.1 | 1.11 · 1.31 · 8.3 |
| has receiver type · expected type | 0.229 · 0.101 | 0.227 · 0.088 |
| mean prompt chars | 1,004 | 869 |
| target identifier coverage | 0.394 | 0.366 |
| leak violations | 0 | 0 |

Identical prompts 42%; editor_snapshot covers a target identifier that strict_prefix does not in 5.5% of samples (mostly
members declared later in the file — legitimate in an editor, but the main leakage-risk surface; the reverse is 0.5%).
Coverage by kind (editor): member_access 0.65, argument_list 0.54, control_flow 0.52, lambda_body 0.52, after_keyword 0.51,
linq 0.44, line_start 0.32, identifier_partial 0.17. Receiver types are resolved for 98% of member_access carets; expected
types for 89% of control_flow and 44% of argument_list carets.

### Safe adhoc vs trusted MSBuild (E2, 970 common samples)

Status agreement 0.89, identical prompts 0.59, receiver type agreement 0.63 and expected type agreement 0.72 where MSBuild
resolved them; coverage 0.33 (adhoc) vs 0.38 (MSBuild). Adhoc lacks NuGet references (EF Core, MediatR, Aspire, gRPC), so
those receivers stay unresolved (`partially_resolved`), but it needs no restore and executes nothing, and it covers MAUI
projects that MSBuild could not load.

### E5 — determinism

Byte-identical `discovery/corpus/samples/exclusions/summary` for sequential run 1 vs run 2 vs 8 workers, and identical
`samples/semantic/summary` for the MSBuild sequential vs 8-worker runs.

## Bottlenecks

1. **Semantic analysis is ~99.7% of CPU in E3** (Σ semantic 1,144 s vs Σ parse+extract 3.1 s). Every analysis forks the
   document (`WithText`) and builds a new compilation snapshot: ~4 MB allocated per analysis (80 GB in E3), which also causes
   the poor parallel scaling (sequential → 8 workers only 1.43×; per-analysis p50 22 → 40 ms under GC/contention).
   Next step: speculative binding of the edited member against the cached original compilation, analyze both policies
   from one fork where possible, Server GC for semantic runs.
2. **Syntax extraction**: candidate generation/filtering costs ~5× parsing (1.53 s vs 0.29 s sequential); 8 workers give 2.3×
   on this small repository, bounded by the sequential writer and discovery (Amdahl).
3. **Validation** (3 s, single-threaded JSON-schema evaluation) takes longer than syntax extraction; parallelize per shard.
4. Fixed during the pilot: quadratic behavior on long statement lists (Roslyn `DescendantTokens(span)`,
   `GetNextToken`, `FindToken` scan sibling lists linearly) — replaced by a flat token index; 10k-line file 61.5 s → 2.35 s.
   Semantic work on samples later dropped as duplicates (−20% E3 work) — dedup now runs before the semantic stage.

## Semantic optimization (2026-10-09, after the run above)

Same machine, Release build, eShop E3 (all 14,071 samples × 2 policies = 28,142 analyses):

| | fork engine (before) | `auto` engines (after) |
|---|---|---|
| pipeline wall, 1 worker | — | **49.6 s** |
| pipeline wall, 8 workers | 230.2 s | **35.8 s** (Server GC: 32.3 s) |
| CPU | 556.9 s | **67.8 s** (1 worker) |
| allocations | 79.8 GB | **7.8 GB** |
| peak RSS | 744 MiB | 645 MiB |
| per-analysis p50 / p95 | 30.0 / 118.7 ms | **0.39 / 12.1 ms** (1 worker) |
| leak violations | 0 | 0 (19,166 records validated) |

Engine mix on the E2 subset: speculative 53%, speculative_scope 30%, statement_scope 12%, fork 4%, fork after speculative error 2%.
Outputs are byte-identical for 1, 8 and 16 workers.

**Scaling inside one repository is limited** (8 workers ×1.4): worker time is 2.3× CPU time, i.e. threads wait on shared
compilation state inside Roslyn; GC is not the limiter. **Scaling across repositories is good**: independent single-worker
processes on the same 16-thread laptop, each running full E3 on eShop:

| concurrent processes | batch wall | total samples/s | speed-up vs 1 | peak RSS per process |
|---|---|---|---|---|
| 1 | 49.6 s | 284 | 1.0× | 645 MiB |
| 4 | 66.8 s | 842 | 3.0× | ≤ 669 MiB |
| 8 | 92.2 s | 1,221 | 4.3× | ≤ 649 MiB |

(8 physical cores / 16 threads; the MSBuild BuildHost exits after workspace load and is not in these numbers.) Operating model
for large runs: one process per repository with 1–2 workers, concurrency bounded by a per-job memory budget.

## TYPE block study (flc-prompt/v2, 2026-10-09)

20% deterministic sample subset per repository; eShop with the trusted MSBuild source, the other five with the safe adhoc
source. "Coverage" = share of identifiers of the hidden target (string literals removed, keywords excluded) that appear in the
**rendered** `<|sem|>` block of samples that have one, i.e. after the char budget is applied (`artifacts/types/measure.py`).

| Repository | v1, 900 chars | v2 (TYPE), 900 | v1, 1500 | v2 (TYPE), 1500 | semantic CPU, TYPE vs none |
|---|---|---|---|---|---|
| eShop | 0.399 | 0.448 | 0.416 | **0.489** | +24% |
| Polly | 0.420 | 0.426 | 0.426 | **0.462** | +21% |
| CleanArchitecture | 0.355 | 0.426 | 0.355 | **0.429** | +15% |
| ILSpy | 0.441 | 0.469 | 0.457 | **0.500** | +14% |
| unity-mcp | 0.410 | 0.453 | 0.419 | **0.474** | −2% (noise) |
| LiteDB | 0.430 | 0.484 | 0.444 | **0.523** | +16% |

Mean rendered semantic block: +100–140 chars at a 900 budget, +250–370 chars at 1500. Gains are largest after operators and
keywords, in argument lists and LINQ (eShop 1500: after_operator 0.46→0.58, after_keyword 0.47→0.61, linq 0.43→0.57,
argument_list 0.51→0.61) and on line starts (0.38→0.45). Leakage validation stayed clean (0 violations; 3,894 eShop and
45,888 LiteDB semantic records). Members declared in the repository rank before inherited library members (e.g. a
`DbContext` subclass shows its `DbSet` properties before `ChangeTracker`). Coverage is an upper-bound helpfulness proxy, not
model accuracy; the final budget should be chosen in tokens once a tokenizer is pinned.
