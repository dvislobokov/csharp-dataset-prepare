# AGENTS.md — C# Full-Line Completion Dataset Builder

## Mission

Build a reproducible, performant, semantic-aware dataset pipeline for training a **small, local, decoder-only C# Full-Line Completion (FLC) model (target: 50–100M parameters)** for an IntelliJ IDEA C# plugin.

**Pilot repository:** the current, actively maintained [`dotnet/eShop`](https://github.com/dotnet/eShop), branch `main`, **not** the archived `dotnet-architecture/eShopOnContainers`. The current eShop reference application uses .NET 10 and has `eShop.slnx`, `src/`, and `tests/`. Resolve and record the precise Git commit SHA before processing; never rely on a moving branch name for dataset identity. Read the checked-out repository's `global.json` to determine the required SDK, instead of assuming one specific patch version.

**Future scale:** a user-supplied manifest of approximately **34,000 C# repositories**. The manifest is not available yet. Architect for it, but **do not** fetch or process those repositories in this phase and **do not invent the manifest**.

The priority is **correctness, data quality, reproducibility, observability, and measured throughput**, not model training or UI integration. Do not implement a transformer, tokenizer, or model runtime in this phase. Preserve data needed for those downstream experiments.

## How Codex should work

1. Inspect the existing working tree before making changes. Follow its conventions when present. If this is an empty project, initialize the smallest sensible .NET repository described below.
2. Execute the pilot end to end; do not stop at an architecture document or stubs. Implement an actual CLI, tests, schema validation, sample extraction, a benchmark and a readable report.
3. Work incrementally: filesystem discovery -> syntax extraction -> data validation -> semantic enrichment -> benchmarks -> scale-ready interfaces. Run tests after each meaningful change.
4. Keep changes scoped to the dataset tool; **never modify eShop sources**. Treat eShop as a read-only input checkout. Never run eShop's services to collect training data.
5. Prefer simple, testable components and streaming iteration over elaborate frameworks. Use C#/.NET with Roslyn as the primary implementation language.
6. If dependencies, network access, an SDK, restore, or workspace loading are unavailable, implement and test the functionality that can run on local fixtures, mark the skipped benchmark explicitly, and document the blocker. **Do not fabricate performance numbers, Git SHAs, successful builds, or semantic coverage.**
7. After each substantial task, report files changed, commands run, tests passed/failed, metrics observed, limitations, and the next concrete task.
8. Do not start training a model, clone bulk repositories, provision cloud infrastructure, or run networked workload services without a separate explicit request.

## Non-goals for the pilot

- No crawling the future 34k-repository list.
- No FIM model training, LLM fine-tuning, GPU requirements, IDE plugin runtime or generation service.
- No downloading NuGet packages or executing untrusted MSBuild targets simply to improve completeness without making execution conditions and security explicit.
- No pretending that Roslyn guarantees a completion is logically correct. It can establish scope, symbols, types, overloads, flow information when available, and reject many invalid completions, but not developer intent.

## Technologies and design constraints

- **.NET:** use a current stable SDK supported by the builder; analyze each target repository according to its own SDK settings. For the pilot, respect eShop's `global.json` and `.slnx`.
- **Roslyn:** `Microsoft.CodeAnalysis.CSharp`, `Microsoft.CodeAnalysis.Workspaces`, and optionally `Microsoft.CodeAnalysis.CSharp.Workspaces` / `MSBuildWorkspace` for trusted, configured projects. Prefer APIs available in the chosen package versions; verify API signatures before using them.
- **CLI:** simple cross-platform `dotnet` console application; Linux, Windows, macOS. No GUI.
- **Storage:** schema-versioned JSONL (optionally `.jsonl.gz` or `.jsonl.zst` when supported) for correctness and interchange; abstract writers so Parquet can be added later. Do not require a database for the pilot.
- **Testing:** unit tests, end-to-end integration tests against synthetic C# fixtures, optional eShop smoke tests, and reproducible performance runs.
- **Security:** input code is untrusted. No execution of analyzed code. Semantic compilation / MSBuild project evaluation may import and execute external targets; gate it behind an explicit trusted-project option and a documented isolation strategy. Syntax-only mode must work without build, restore, or code execution.

## Suggested repository layout

Adapt to an existing project if necessary, otherwise use:

```text
AGENTS.md
README.md
src/
  FlcDataset.Cli/              # command parsing, orchestration
  FlcDataset.Core/             # schema, records, config, deterministic IDs
  FlcDataset.Discovery/        # repository, project, file enumeration
  FlcDataset.Extraction/       # Roslyn syntax analysis and caret sampling
  FlcDataset.Semantics/        # semantic provider and feature selection
  FlcDataset.Storage/          # JSONL writers, checkpoints, manifests
  FlcDataset.Benchmarks/       # benchmark scenario(s) / harness
 tests/
  FlcDataset.UnitTests/
  FlcDataset.IntegrationTests/
fixtures/                       # small synthetic C# samples
configs/
  eshop.pilot.json
schemas/
  flc-sample.v1.schema.json
  corpus-file.v1.schema.json
  run-manifest.v1.schema.json
docs/
  DATASET_SPEC.md
  EXTRACTION_RULES.md
  BENCHMARKS.md
  SCALE_PLAN.md
artifacts/                      # ignored: datasets and profiling results
workspaces/                     # ignored: checked-out pilot repositories
```

Do not add a separate project merely to satisfy the layout if a smaller organization is clearly cleaner. Boundaries and tests matter more than assembly count.

## Two dataset products — keep separate

### A. Source corpus for causal pretraining

One record per accepted source file and repository revision. Preserve **unaltered original content** (including BOM and newline style if byte preservation is supported) or retain enough source metadata to recover it. Any normalized derivative must be explicitly marked as such. The following is a *logical schema example*:

```json
{
  "schema_version": "corpus-file/v1",
  "repository_id": "github.com/dotnet/eShop",
  "revision": "<resolved-full-commit-sha>",
  "relative_path": "src/Catalog.API/Program.cs",
  "language": "csharp",
  "sha256": "<hash-of-exact-source-bytes>",
  "license": "MIT",
  "encoding": "utf-8",
  "content": "var builder = WebApplication.CreateBuilder(args);\n"
}
```

A `<...>` value here means a field to compute during extraction, not a literal value to emit. Use a real license value only when verified; otherwise record `null` / `unknown` and a corresponding reason. Prefer referencing archived source blobs in future scale mode to repeating file content in every FLC sample.

### B. Caret-based Full-Line Completion samples

Produce samples from real source text and **replayable editor positions**, not artificially paraphrased code. Every sample must describe the exact source revision, file, caret position, context, target, and extraction strategy.

Example (illustrative, not a literal eShop excerpt):

```json
{
  "schema_version": "flc-sample/v1",
  "sample_id": "<stable-content-derived-id>",
  "repository_id": "github.com/dotnet/eShop",
  "revision": "<resolved-full-commit-sha>",
  "relative_path": "src/Example/OrderService.cs",
  "source_sha256": "<hash-of-exact-source-bytes>",
  "caret_utf16_offset": 124,
  "caret_line_zero_based": 6,
  "caret_column_utf16_zero_based": 21,
  "caret_kind": "member_access",
  "left_context": "return await repository.Get",
  "target_text": "ByIdAsync(id);",
  "right_context": "\n}",
  "end_of_line": "LF",
  "semantic_status": "not_attempted",
  "semantic": null,
  "split": "pilot"
}
```

**The `left_context` value above is shortened for readability.** Actual records should contain the configured preceding context ending at the caret, and must record whether that context was truncated. Preserve the original source separately. The `right_context` starts immediately after the `target_text` and **includes** the line ending if present. `target_text` is ONLY the missing suffix of the physical current line, **without a newline**. This representation is unambiguous: reconstruct the source as `source_before_caret + target_text + source_after_target`; store exact offsets needed to prove this invariant.

For supervised FLC, serialize `target_text` followed by a dedicated **`<EOL>` stop token** (or a documented equivalent); the model's inline suggestion MUST NOT insert the stop token. The ordinary causal pretraining corpus keeps physical newline characters as source text. The chosen training adapter should compute loss only on the FLC target and stop token, not on prompt/context tokens. No tokenizer implementation is required in this phase.

Include fields or a sidecar for source byte spans, content hashes, split origin, quality flags, generation config version, and provenance. Place potentially large `semantic` payloads in separate records keyed by `sample_id` if this improves memory or storage efficiency. A JSON schema must define mandatory fields, units, nullability, and any truncation rules.

## Critical offset and newline rules

- Roslyn source spans and positions are **UTF-16 code-unit offsets**. Git/file hashes are based on original bytes. Explicitly track the distinction. Test non-BMP characters, Unicode identifiers and strings, tabs, CRLF, LF, BOM and EOF without terminal newline.
- All carets must be valid source positions. `line` and `column` are **zero-based**; state this in the schema.
- `target_text` must not include `\r` or `\n`. Reject multiline targets for the one-line dataset. Raw multiline strings are excluded initially and tracked in skip statistics.
- Avoid quietly trimming or reformatting whitespace. Keep the exact original target including spaces and punctuation. The selected context length can be changed later without corrupting the canonical source span.
- Record both source coordinates and any materialized left-context string. Exact reconstruction tests operate on the **full source**, not on a possibly truncated model window.
- Define whether whitespace at a caret is already typed or remains part of the target. Reproducibility is more important than a specific convention.

## Caret-position generation

Implement deterministic and configurable sampling with a stable seed. At least the following strata:

1. Start of nonblank code line after indentation.
2. Partially typed identifier (including internal character offsets).
3. Immediately after `.`, `?.`, or relevant member-access operator.
4. Inside argument lists and between arguments.
5. After `return`, `await`, `new`, assignment, `=>`, and similar constructs.
6. Inside LINQ chains and common modern C# constructs.
7. Control-flow and exception-handling expressions.
8. Optional negative/unsupported strata: comment, regular string, interpolated string, raw string, preprocessor directive. **Tag and count exclusions**, do not silently discard.

Choose positions by syntax node/token boundaries with a small controlled amount of character-level sampling. Do **not** create a training example at every character: excessive near-duplicate samples from long identifiers bias the dataset. Define maximum candidate positions per line, sampling weights, and deterministic capping per file/project/repo; configure rather than hard-code weights. Stratify output metrics by caret category.

Before materializing a sample, require: non-empty target unless explicitly generating a no-suggestion dataset; reasonable line length; well-defined caret-to-line-end span; safe source encoding; accepted license/provenance; no known secrets. Exclude or flag generated sources (`**/obj/**`, `**/bin/**`, `**/*.g.cs`, `**/*.Designer.cs`, migrations/snapshots and other patterns through config), vendored and duplicate files. Do not blanket-exclude `tests/`; measure test-code contribution separately. Distinguish code generated by tools from ordinary code without relying solely on a filename convention.

## Semantic-aware extraction — the essential experiment

**Goal:** teach the model what identifiers, types and callable members exist at the caret without leaking the completion target. The semantic pipeline is a separate, measurable stage and must support an explicit mode:

- `syntax_only`: no project loading, no restore/build; always usable.
- `semantic_best_effort`: attempt analysis using a trusted, configured workspace; fall back with a **reason-coded** failure without dropping the syntax sample.
- `semantic_required`: fail samples that cannot be semantically analyzed, with counts and reasons.

Never label syntax-only extraction as semantically resolved.

At each caret, make an **editor-equivalent snapshot** before querying semantic information: remove or replace **the not-yet-typed suffix of the current line** (the target), preserving the rest of the file if using an editor snapshot. Treat any semantic information accidentally derived from the removed target as **label leakage**. Do not run semantic analysis on the original complete line and then claim its result describes the incomplete editing state.

Define and implement two visibility policies, making them explicit per sample:

- `editor_snapshot`: current incomplete line plus code after the line remains in the document (as it would in an editor); the target is removed. This supports cross-file and later-declaration awareness but requires leakage auditing.
- `strict_prefix`: no code after the caret can influence semantic features. This is useful as a leakage-control ablation; semantic resolution may be incomplete or unavailable because code is syntactically unfinished. Record degraded confidence rather than inventing facts.

For syntax-only samples, the **raw model prompt should default to left context only**; do not provide unredacted `right_context` as a model feature. Saving right context for future FIM research is allowed.

Candidate semantic fields (emit only those correctly resolved):

```json
{
  "status": "resolved",
  "visibility_policy": "editor_snapshot",
  "enclosing_symbol": "GetOrderAsync(System.Guid)",
  "return_type": "System.Threading.Tasks.Task<OrderDto>",
  "expected_type": "OrderDto",
  "locals": [
    { "name": "order", "type": "Order", "kind": "local", "nullable_flow_state": "not_null" }
  ],
  "parameters": [
    { "name": "id", "type": "System.Guid", "kind": "parameter" }
  ],
  "receiver_type": "IOrderRepository",
  "members": [
    { "name": "GetByIdAsync", "kind": "method", "signature": "GetByIdAsync(Guid):Task<Order?>" }
  ]
}
```

This is illustrative: resolve nullable flow state and expected expression type **only when the Roslyn APIs and editing state justify them**. Do not conflate a declared nullable annotation with proven non-null flow state. `expected_type` is a best-effort contextual inference, not a universally available built-in property. Support ambiguity (`candidates`, `confidence` / `resolution_status`) and multiple overloads; do not force a single guessed overload.

Use `SemanticModel.LookupSymbols`, `GetTypeInfo`, `GetSymbolInfo`, `GetEnclosingSymbol`, appropriate Roslyn recommendations/completion APIs and speculative binding where applicable. Filter inaccessible members and inapplicable candidates; do not treat all members of a named type as completion-eligible. Keep a compact serialized semantic prompt representation separate from the canonical structured facts, e.g. `LOC order:Order; ARG id:Guid; RECEIVER repo:IOrderRepository`.

**Hard leakage tests:** a local/variable declared *inside the target* cannot appear in scope facts; a method invocation present only in the removed suffix cannot be copied into the semantic payload; model features must not expose the expected answer verbatim from the removed text. Naturally valid symbol names that also appear in the answer may appear when they genuinely exist independently in the project—do not incorrectly label all such overlaps leakage. Compare `editor_snapshot` against `strict_prefix`.

Project-loading safety/performance:

- eShop is the **trusted pilot only**; `MSBuildWorkspace` may require matching .NET SDK, restores and project evaluation. Make opt-in trusted workspace evaluation an explicit command/config flag.
- Keep per-solution/project compilations and semantic models cached where safe. Do not re-open/reload the solution for every caret. When a sample needs a modified document, use immutable Roslyn solution/document snapshots and share only unmodified structures when possible.
- Report whether a sample was `resolved`, `partially_resolved`, `syntax_fallback` or `failed` and why (`missing_reference`, `project_load`, `parse_error`, `unsupported_caret`, `timeout`, etc.).
- Never let workspace loading or semantic errors crash the whole repository job by default.

## Pilot experiments on current dotnet/eShop

Analyze the pinned checkout across `src/**/*.cs` and `tests/**/*.cs`; discover exact project/file counts instead of assuming them. eShop contains multiple services and layers including API, domain, infrastructure, and test projects. Report distributions by project and category. Start with the cheapest mode and only then enable semantic extraction.

Implement these experiments with identical accepted source files and, where applicable, identical sampled carets:

| Experiment | Description | Purpose |
| --- | --- | --- |
| E0 | File discovery + filtering + corpus manifest | Establish reliable source counts and input-byte volume |
| E1 | Syntax-only extraction | Establish correct canonical FLC samples and peak throughput |
| E2 | Semantic enrichment of a fixed sample subset | Measure extra value and cost of type/scope information |
| E3 | Full eligible semantic enrichment (if feasible) | Measure realistic scaling, fallbacks, and cache efficiency |
| E4 | `editor_snapshot` versus `strict_prefix` on matching samples | Detect leakage risk / information loss |
| E5 | Re-run same revision/config/seed | Prove determinism and idempotence |

Track **at least**: repository SHA; SDK/Roslyn versions; OS/CPU/RAM; worker count; mode; seed; exact config; elapsed wall time; CPU time if available; peak managed and resident memory; bytes read/written; discovered/accepted/skipped files (by reason); lines and tokens if a tokenizer is configured (otherwise lines/chars, do not fabricate token counts); candidate positions; accepted examples; examples/sec; source MiB/sec; p50/p95/p99 extraction latency where measurable; semantic resolved/fallback rates; cache hit rate if caching is implemented; output size; duplicate fraction; and schema/reconstruction validation failures.

Use warm and cold runs, where possible. Separate **checkout, dependency restore/project load, discovery, syntax processing, semantic processing, serialization, compression, and final validation** timings. Prevent background indexing/restore time from being mislabeled extraction throughput. Record machine conditions and limitations. A baseline using only sequential workers must exist before experimenting with parallelism.

### Performance acceptance criteria

Do not invent an absolute `examples/sec` target without measurements. The pilot must:

- Emit deterministic, schema-valid output on identical input/config/seed.
- Complete the syntax-only pipeline without requiring the eShop app to run.
- Produce a machine-readable `benchmark.json` and concise Markdown comparison for E0–E2 at minimum, or explicitly explain any blocked mode.
- Include memory/time profiling sufficient to identify the largest bottleneck(s).
- Compare sequential versus bounded parallel execution without unbounded in-memory queues.
- Demonstrate that semantic extraction costs are separately observable and failures do not silently pollute records.

## Scale-ready design for 34,000 repositories — interfaces now, execution later

Define a manifest reader interface accepting user-supplied `txt`, `csv`, or `jsonl` records, with repository URL/ID and optional revision, license/provenance, include/exclude filters and priority. Do not assume all entries are GitHub-hosted, public, buildable, or licensed for training.

Prepare a **separate, later** ingestion workflow with:

- Allowlisted URL schemes/hosts, sanitized paths, no shell injection, minimal credential exposure.
- Checkout pinned commits, shallow/fetch policy where applicable, quotas/size limits, configurable concurrency and timeouts, exponential backoff/jitter and rate limiting, and Git host API-limit awareness.
- Retryable, idempotent states: `pending`, `cloning`, `ready`, `processing`, `complete`, `skipped`, `failed` with error reason and attempt counters.
- Atomic shard writes, checksums, restartable checkpoints, and a durable run manifest so interrupted jobs resume without duplicates.
- **Repository-group splits**, fork lineage/grouping and exact/near duplicate detection across repositories to prevent test contamination. Split before caret expansion, never randomly split individual samples from the same file/repo.
- Per-file, per-project, per-repo sampling caps; per-repo quality/weighting and dedup to avoid overweighting giant or copied codebases.
- License allowlist with retainable source attribution; secrets/PII scanning; configurable exclusions and audit logs. Unknown or restricted licenses must not silently enter the training corpus.
- Bounded discovery -> parse -> semantic -> validate -> write pipeline; memory backpressure; controlled worker limits; per-stage counters, metrics and graceful cancellation.
- Configurable semantics: syntax-only baseline for all repositories; trusted sandboxed project-aware mode for eligible repos; fallback for broken, missing-SDK or unsafe projects.
- Semantic caches scoped to repository revision, project compilation options, source snapshot and target caret; no cross-revision cache confusion.

At this phase write `docs/SCALE_PLAN.md` and interfaces/tests needed to preserve compatibility, **but do not write an automated 34k bulk downloader unless explicitly requested later**.

## CLI contract (proposed)

Make command names discoverable through `--help`. Support these flows or close documented equivalents:

```bash
# Fetch only the known pilot, pin and record the resulting SHA.
dotnet run --project src/FlcDataset.Cli -- pilot fetch \
  --url https://github.com/dotnet/eShop.git --ref main --dest workspaces/eshop

# Syntax-only extraction; safe by default and independent of app startup.
dotnet run --project src/FlcDataset.Cli -- extract \
  --repo workspaces/eshop --mode syntax_only --config configs/eshop.pilot.json \
  --out artifacts/eshop-syntax

# Semantic mode requires an explicit trust opt-in.
dotnet run --project src/FlcDataset.Cli -- extract \
  --repo workspaces/eshop --mode semantic_best_effort \
  --trusted-project-evaluation --config configs/eshop.pilot.json \
  --out artifacts/eshop-semantic

# Validate exact source reconstruction, identifiers, schema and splits.
dotnet run --project src/FlcDataset.Cli -- validate \
  --dataset artifacts/eshop-syntax --repo workspaces/eshop

# Measure reproducibly; emit JSON metrics + Markdown summary.
dotnet run --project src/FlcDataset.Cli -- benchmark \
  --repo workspaces/eshop --config configs/eshop.pilot.json \
  --out artifacts/benchmarks
```

Use atomic outputs and refuse accidental overwrite unless `--overwrite` is explicitly selected, or create an unambiguous run ID. Prefer stable defaults and clear errors to confusing option proliferation.

## Tests and gates

Tests must include all of the following:

1. `prefix + target + suffix` reconstruction at the configured caret using **original bytes/source text**, including Unicode, LF/CRLF and EOF cases; test full-source spans when context windows are truncated.
2. UTF-16 offset/line/column round trips, including surrogate pairs, tabs and BOM.
3. Every target ends within its physical line; `target_text` contains no newline; no accidental duplicated characters on insertion.
4. Same SHA + config + seed yields the same stable sample IDs, shard contents, sample order (or a documented deterministic sorting), and summary.
5. File exclusion, license gate, deduplication and category sampling use counters and explain skipped samples.
6. No semantic leakage from erased target. Correct scope for locals and parameters; correct type recognition for `var`; member-access/overload tests; nullability status only when legitimately derived.
7. Semantic best-effort fallback survives missing SDK, failed project load, malformed source and missing references.
8. Bounded memory behavior in large synthetic files; cancellation and safe write interruption.
9. Per-repository split isolation and cross-repo duplicate/fork grouping behavior (synthetic manifests sufficient now).
10. JSON schema validation, run manifest/checksum verification, resumed/idempotent outputs.

Prefer small standalone synthetic projects and fake semantic providers for deterministic tests. eShop smoke tests should verify realistic behavior, but should not be the only correctness oracle. **No suite requiring cloud resources, containers, Azure, external secrets, or live application services** is necessary for a dataset builder.

## Required deliverables before considering the pilot complete

- Working CLI implementing fetch/pin (or documented manual checkout fallback), discover, syntax extract, validate, and benchmark.
- Structured schema v1, corpus and FLC JSONL sample outputs, run manifest and documented storage layout.
- Real Roslyn-based caret extraction with well-defined sampling rules, stable IDs and exact source reconstruction.
- Real semantic context prototype for at least several positions in eShop, or reason-coded explicit limitations if the environment blocks project load.
- Documentation: data contract, extraction strategy, trust/security, reproducibility, semantic leakage safeguards, scale plan and benchmark methodology.
- Tests with passing output; a reproducible pilot recipe. Do not claim tests passed unless they were run.
- Measured comparison **syntax-only vs semantic** with actual timings and resource observations where executable, including coverage and top bottlenecks. Empty/unmeasured tables are not a completed benchmark.
- A prioritized follow-up plan: optimizations, data quality experiments, and later 34k manifest ingestion.

## Definition of done: implementation behavior

An engineer can clone this dataset-builder project, acquire or point it to a pinned current eShop checkout, execute a documented command, obtain valid corpus/FLC samples plus provenance/quality reports, rerun without changing sample identity, and understand where time/memory were spent. Semantic mode is explicit, auditable and does not derive features from the hidden target. The project is designed for future bulk ingestion but has not yet executed it.

## Useful upstream references

- `https://github.com/dotnet/eShop` — **current** .NET/Aspire eShop sample, pilot data only.
- `https://github.com/dotnet/eShop/blob/main/eShop.slnx` — solution layout (verify at pinned SHA).
- `https://github.com/dotnet/eShop/blob/main/global.json` — project SDK selection (verify at pinned SHA).
- `https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/` — Roslyn concepts/APIs.

Keep this file current whenever the dataset schema, leakage policy, or acceptance criteria change. If an existing implementation conflicts with this specification, explain the conflict and update the docs/tests as part of the change rather than silently diverging.

## Implementation decisions (kept current; pilot implementation 2026-10)

These refine or deviate from the proposals above; docs and tests were updated accordingly.

- **Layout:** `Discovery` and pipeline/storage code live in `FlcDataset.Extraction` and `FlcDataset.Core`; benchmarks are a CLI
  command (`benchmark`) running each experiment as an isolated child process. One test project (`tests/FlcDataset.Tests`)
  holds unit and integration tests. Pilot checkout lives at `data/repos/eShop` (ignored), not `workspaces/`.
- **Target convention:** target = rest of the physical line after the caret **excluding trailing whitespace** (which starts
  `right_context`). Indentation before a `line_start` caret counts as typed. See `docs/DATASET_SPEC.md`.
- **Stop token:** dedicated `<|eol|>` (not `\n`) in `flc-prompt/v1` (`docs/PROMPT_FORMAT.md`); multi-line reserves
  `<|end_completion|>`. Canonical records never store a preformatted prompt; `render` produces model records.
- **Schemas:** `flc-sample/v1`, `corpus-file/v1`, `flc-semantic/v1` (sidecar), `run-manifest/v1` under `schemas/`.
- **Semantic tiers:** `--semantic-source adhoc` (no MSBuild/NuGet/code execution, framework references only) in addition to the
  trusted `msbuild` source gated by `--trusted-project-evaluation`.
- **strict_prefix** appends closing braces computed from the prefix only (`synthetic_suffix`), otherwise the caret lands on
  the EOF token outside every member.
- **Recovery artifacts:** in `editor_snapshot`, symbols declared after the caret are kept only if the original document declares
  the same kind/name at the shifted location (drop-only use of the original), counted in `dropped_recovery_artifacts`.
- **Splits (pilot):** grouped by project family; eval families pinned in `configs/eshop.pilot.json`.
- **Semantic engines:** `semantic.engine=auto` uses statement_scope / speculative / speculative_scope binding against the original
  compilation (never binding the original edited line) and falls back to the document fork; see `docs/EXTRACTION_RULES.md`.
  `fork` remains selectable as the reference implementation (`--semantic-engine fork`, `semantic-compare` to diff).
