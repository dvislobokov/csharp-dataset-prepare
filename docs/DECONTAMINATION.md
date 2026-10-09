# Near-duplicate decontamination and dependency profiles (2026-10-09)

## Near duplicates: held-out splits vs train (`scripts/near_dup.py`)

Exact cross-repository duplicates were removed during extraction (file hash ownership). This pass finds edited copies:
code tokens, 5-token shingles, MinHash 128 permutations, LSH 16 bands x 8 rows over the train signatures, candidates verified
by the MinHash Jaccard estimate >= 0.8 (the engine's dedup threshold). Runtime ~2.5 min per language on 20-24 cores.

| | validation files flagged | test files flagged | repositories mostly duplicated (>= 50 % of >= 5 files) |
|---|---|---|---|
| C# | 1 630 / 43 218 (3.8 %) | 1 202 / 41 821 (2.9 %) | val: PicoXLSX, XCharts, unity-netcode-benchmark, BCnEncoder.NET; test: ASP.NET-Core-Template, nunit.testlogger, ChromaDB.Client |
| Go | 1 233 / 47 297 (2.6 %) | 870 / 29 859 (2.9 %) | val: rueidis, xray-plugin; test: gitops-operator, rtreego |

Typical pairs are forks and vendored copies (ClassicUO -> TazUO, Unity-Framework -> TEngine, Fare inside CrypTool-2).
**Use:** drop the flagged files (and the "mostly duplicated" repositories) from every evaluation; training data is unchanged.
Files: `decontam/near_duplicates.jsonl` (one row per flagged file with its closest train file and the estimate),
`decontam/summary.json`, `decontam/repos-<set>.json` in both HF datasets. Files shorter than 10 tokens are never flagged.

## Dependency profiles (`scripts/deps_profile.py`, docs/CONTEXT_SPEC-RU.md section 4)

Per corpus file: the external library roots it imports (own namespaces/module and the standard library removed); the DEPS
line of a file = roots used in >= 2 OTHER files of its repository, top 12, <= 200 chars (`profile_for`).

| | repositories | files | files with a non-empty profile |
|---|---|---|---|
| C# | 8 140 | 2 349 577 | 98.9 % |
| Go | 8 928 | 1 832 170 | 97.8 % |

Most frequent roots (repositories): C# Xunit, Newtonsoft, Microsoft.Extensions.Logging, Microsoft.Extensions.DependencyInjection,
Microsoft.AspNetCore.Mvc, UnityEngine, NUnit, Moq, Microsoft.EntityFrameworkCore, Serilog, FluentAssertions;
Go testify, cobra, google/uuid, yaml.v3, golang.org/x/*, prometheus client, grpc, k8s client-go, pkg/errors, logrus, viper, zap.
Files: `deps/file_roots.parquet` (repository_id, relative_path, sha256, roots), `deps/repo_roots.parquet`, `deps/summary.json`.
Go module paths are not in the corpus (no go.mod): own packages are recognised by the repository id or, for vanity paths,
by import paths that end with a directory of the repository.
