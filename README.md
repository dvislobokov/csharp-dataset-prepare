# flc-dataset — C# full-line completion dataset builder

Roslyn-based pipeline that turns C# repositories into (A) an exact source corpus for causal pretraining and (B) caret-based
full-line completion samples with optional, leakage-audited semantic context, for a small local completion model
(50–100M parameters) in an IntelliJ IDEA plugin. Pilot: [`dotnet/eShop`](https://github.com/dotnet/eShop) pinned at
`dc7ea499cd356924fb6689b3702964a5869dbae9`.

## Layout

```
src/FlcDataset.Core        records, config, hashing, JSONL writers, metrics, prompt renderer, scale contracts
src/FlcDataset.Extraction  repository inspection, discovery/filters, Roslyn caret extraction, bounded pipeline
src/FlcDataset.Semantics   MSBuild (trusted) and adhoc (safe) workspaces, snapshot semantic analyzer, enricher
src/FlcDataset.Cli         `flc-dataset` CLI: pilot fetch, discover, extract, validate, inspect, render, compare, benchmark
tests/FlcDataset.Tests     unit + integration tests on synthetic fixtures (fixtures/repo1)
schemas/                   JSON schemas v1          configs/   eshop.pilot.json
docs/                      DATASET_SPEC, EXTRACTION_RULES, PROMPT_FORMAT, SECURITY, BENCHMARKS, SCALE_PLAN
artifacts/ (ignored)       datasets, benchmarks, logs     data/repos/ (ignored) input checkouts
```

## Requirements

.NET SDK 10 (tool: `global.json` → 10.0.100+, latestFeature). eShop's own `global.json` requires 10.0.302+ (latestFeature);
the pilot used **10.0.401** installed to `~/.dotnet` (`dotnet-install.sh --channel 10.0 --quality GA`).

```bash
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet build -c Release
dotnet test
alias flc=src/FlcDataset.Cli/bin/Release/net10.0/flc-dataset
```

## Pilot recipe

```bash
# 1. Pin the pilot (reuses an existing checkout; records the SHA in artifacts/pilot-pin.json)
flc pilot fetch --url https://github.com/dotnet/eShop.git --ref main --dest data/repos/eShop

# 2. E0 + E1: safe, no build/restore/code execution
flc discover --repo data/repos/eShop --out artifacts/eshop-e0
flc extract  --repo data/repos/eShop --out artifacts/eshop-syntax --workers 8
flc validate --dataset artifacts/eshop-syntax --repo data/repos/eShop

# 3. Semantic, safe tier (no MSBuild)
flc extract --repo data/repos/eShop --out artifacts/eshop-adhoc --workers 8 --mode semantic_best_effort --semantic-source adhoc

# 4. Semantic, trusted tier: restore once (executes NuGet/MSBuild targets; MAUI projects need workloads and are skipped)
(cd data/repos/eShop && for p in $(grep -o 'Path="[^"]*csproj"' eShop.slnx | sed 's/Path="//;s/"//' | grep -v -E 'ClientApp|HybridApp'); do dotnet restore "$p"; done)
flc extract --repo data/repos/eShop --out artifacts/eshop-semantic --workers 8 \
    --mode semantic_best_effort --trusted-project-evaluation        # solution from config: eShop.Web.slnf

# 5. Model-facing records (flc-prompt/v1) + 100-example preview.md
flc render --dataset artifacts/eshop-semantic --out artifacts/eshop-prompts
flc inspect --dataset artifacts/eshop-semantic --kind member_access --count 5

# 6. Full benchmark E0–E5 (isolated child processes) → artifacts/benchmarks/benchmark.{json,md}
flc benchmark --repo data/repos/eShop --out artifacts/benchmarks --workers 8 --trusted-project-evaluation
```

Outputs are written atomically; an existing `--out` is refused unless `--overwrite` is passed. Same SHA + config + seed →
byte-identical `discovery/corpus/samples/exclusions/semantic/summary` (checked by `flc compare` and the benchmark's E5).

## Key decisions

* Target = rest of the physical line, no newline, trailing whitespace excluded; exact reconstruction is validated against the
  original bytes for every sample (UTF-16 offsets + byte offsets, BOM/CRLF/non-BMP aware).
* Model prompt = left context only + semantic facts computed on a target-free snapshot; stop token `<|eol|>`
  ([PROMPT_FORMAT.md](docs/PROMPT_FORMAT.md)).
* Splits by project family (pilot) / repository group (scale), assigned before caret expansion.
* Semantic modes are explicit; failures are reason-coded and never drop syntax samples in best-effort mode.

Results: [docs/BENCHMARKS.md](docs/BENCHMARKS.md). Security model: [docs/SECURITY.md](docs/SECURITY.md).
