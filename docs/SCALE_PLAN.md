# Scale plan: ~34,000 repositories (design; not executed)

Nothing in this phase downloads or processes the bulk list. Implemented and tested now: manifest reader, job state store,
repository grouping/splitting (`src/FlcDataset.Core/Scale.cs`, `tests/.../ScaleTests.cs`), and a per-repository pipeline
that is already deterministic, bounded and restart-safe at the shard level.

## Workflow

```
manifest (txt/csv/jsonl) ──validate──▶ job store (pending)
   └─ license/provenance gate ─▶ skipped(reason)
pending ─▶ cloning (pinned SHA, blobless/shallow fetch, size quota, timeout) ─▶ ready
ready ─▶ processing: discover → parse → [semantic: adhoc | trusted sandbox] → validate → write shard ─▶ complete
any ─▶ failed(error, attempts++) ─▶ pending (exponential backoff + jitter, max attempts)
```

* **Manifest**: `RepositoryManifestReader` — allowlisted scheme/host, canonical id `host/owner/name`, no credentials, revision
  validated, duplicates rejected, fork lineage via `fork_of`. Rejections are reason-coded.
* **Jobs**: `JsonlJobStore` — append-only, last-state-wins, legal transitions enforced, torn tail ignored; `Resumable()` lists
  pending/interrupted/retryable jobs. A run restarts from the log without duplicating completed shards.
* **Fetch** (later): `git clone --filter=blob:none --no-checkout` + `checkout <sha>`; per-host concurrency and rate limits,
  honor API rate-limit headers, quotas on repo size and file count, credentials only via a scoped helper, never in URLs/logs.
* **Shards**: one output directory per (repository, revision, config hash); atomic rename; checksums in the run manifest; a
  global index lists shard checksums. Content-derived sample ids make re-processing idempotent.

## Splits and contamination

1. Group repositories before any caret expansion: forks join upstream (`RepositoryGrouper.AddFork`); repositories whose file
   hash sets overlap by Jaccard ≥ threshold are merged (`AddContentOverlap`).
2. Assign train/eval/test per group (`SplitOf`), never per sample or file.
3. Cross-repository exact dedup by file sha256 before sampling (first occurrence by priority/id order); near-duplicate files
   via MinHash over normalized token shingles (next step), clustered with the same union-find.
4. Inside a repository, keep project-family grouping (pilot behavior) for repo-internal ablations only.

## Sampling caps and weighting

Per file (`max_samples_per_file`), per project (`max_samples_per_project`), and a per-repository cap proportional to
`sqrt(accepted_lines)` so giant or copied codebases do not dominate. Test code share is measured separately.

## Semantics at scale

* Baseline for all repositories: syntax-only.
* Default semantic tier: **adhoc** (no code execution). Measured on eShop it agrees with MSBuild on most samples when
  references are framework-only; package-heavy code degrades to `partially_resolved` (reason-coded).
* Trusted tier: MSBuild workspace in a sandbox (see SECURITY.md) for allowlisted, restorable repositories with a matching SDK
  (read `global.json`; install SDK bands on demand in the image).
* Caches keyed by `(repository, revision, project, compilation-options hash)`; semantic results keyed by
  `(sample_id, policy, extractor version)`. Never shared across revisions.

## Throughput budget (from the pilot; see BENCHMARKS.md)

Syntax-only throughput is I/O-free CPU work and scales with workers; semantic cost is dominated by per-caret forked
compilations. Planned optimization: speculative binding of the edited member body against the cached original compilation
(`SemanticModel.TryGetSpeculativeSemanticModel`) instead of forking the document for every caret.

## Observability

Per-stage counters and timings are already emitted per run (`run-manifest.json`, `run.log.jsonl`). At scale: aggregate per
shard into a metrics table (repo, state, attempts, timings, sample counts, semantic rates, failures by reason), with graceful
cancellation (already supported via `CancellationToken`) and bounded queues (already `2 × workers` files in flight).
