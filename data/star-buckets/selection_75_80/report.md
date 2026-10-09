# C# repository selection report

Metadata-only selection over `data/csharp-search.jsonl`. No repository was cloned, fetched, or executed; descriptions were treated as untrusted data and only pattern-matched. Reproduce with `python3 -I data/selection/selection_script.py`.

## Funnel

- Input rows: **272**
- After exact-duplicate-row dedup: **272** unique `full_name` (0 duplicate rows removed; search-pagination overlap)
- Selected: **270** (in 270 dedup groups)
- Rejected: **2**
  - license_unknown bucket: **0**
  - restricted_license bucket: **0**

## Rejection reasons (a repo may carry several)

| reason | count |
| --- | ---: |
| abuse_keyword | 1 |
| noncode_or_list | 1 |

## Thresholds & justification (from observed distributions)

- **License gate**: permissive allowlist exactly as specified (0BSD, Apache-2.0, BSD-2-Clause, BSD-3-Clause, ISC, MIT, MS-PL, Unlicense, Zlib). Copyleft/reciprocal -> restricted. NOASSERTION/null and permissive-ish ids NOT on the allowlist (MIT-0, WTFPL, CC0-1.0, BSL-1.0, PostgreSQL, AFL-3.0, CC-BY-4.0) -> license_unknown for manual verification. Observed licenses are dominated by MIT (3716) with sizeable NOASSERTION (772) and null (715) tails, so the unknown bucket is intentionally large.
- **Activity cutoff** = 2024-10-09 (24 months). The corpus spans only 2025-06..2026-10, so this gate passes everything today; recency instead feeds the priority score. Kept as an explicit gate for the future 34k manifest.
- **Size**: repo size is KB incl. git history (permissive p5=178, median=9727, p90=208137). Reject only `< 30 KB AND no description AND no topics` (trivial), flag `< 500 KB` as `tiny`, flag `> 1000000 KB` as `large`. Game/Unity repos `> 300000 KB` are flagged `assets_heavy_risk` (C# likely a minority of bytes) and only rejected when `> 8000000 KB` (asset dump). Legitimate large repos (roslyn, azure-sdk) are deliberately kept.
- **Owner cap** = 15 selected repos/owner (keep highest priority). Prevents a few orgs (microsoft 147, dotnet 79, Unity-Technologies 56, Azure 44 candidates) from dominating the corpus.
- **Spam/abuse**: word-boundary keyword + topic match with benign exceptions (malware-analysis, dependency-injection, packet libraries, cheat-engine clones are NOT rejected). High precision by design; borderline offensive-security tooling is rejected under `abuse_*` and listed for audit.
- **Non-code**: awesome-lists, roadmaps, cheat-sheets, slide/presentation and curated-list repos -> `noncode_or_list`. Educational repos (tutorial/course/learn) contain real C# and are KEPT but flagged `educational` and down-weighted (quality*0.6).
- **Dedup/grouping**: union-find over (a) identical normalized description len>=40 (mirrors/clones) and (b) same repo name + description Jaccard>=0.5 (template families). Canonical = highest priority, org over user on ties; others -> `duplicate_of_group` but keep a shared `group` id for split isolation.

## Selection — category distribution

| category | count |
| --- | ---: |
| other | 82 |
| game/unity | 40 |
| library/sdk | 36 |
| desktop-tool | 25 |
| desktop-ui | 21 |
| web/aspnet | 15 |
| devtools | 15 |
| ml/ai | 11 |
| data/db/orm | 8 |
| networking | 5 |
| cloud/azure | 4 |
| testing | 3 |
| security | 3 |
| iot/embedded | 2 |

## Selection — license distribution

| license | count |
| --- | ---: |
| MIT | 226 |
| Apache-2.0 | 36 |
| BSD-3-Clause | 4 |
| 0BSD | 1 |
| BSD-2-Clause | 1 |
| Unlicense | 1 |
| MS-PL | 1 |

## Selection — top owners (after cap)

| owner | count |
| --- | ---: |
| microsoft | 3 |
| dennisdoomen | 3 |
| PlayFab | 2 |
| Papyrine | 2 |
| ironmansoftware | 2 |
| NixaVulpi | 2 |
| photo-cli | 1 |
| resend | 1 |
| ScoopXDev | 1 |
| PixelWizards | 1 |
| dotnet | 1 |
| loresoft | 1 |
| masastack | 1 |
| Milvasoft | 1 |
| lantean-code | 1 |
| nforgeio | 1 |
| Quaver | 1 |
| Occurify | 1 |
| OrleansContrib | 1 |
| CodingWithCalvin | 1 |

## Selection — quality flags

| flag | count |
| --- | ---: |
| tiny | 70 |
| no_desc_no_topics | 13 |
| educational | 6 |
| assets_heavy_risk | 2 |
| large | 1 |

## Borderline cases (kept, flagged for later review)

- `Project-PLATEAU/PLATEAU-SDK-Toolkits-for-Unity` (cat=game/unity, flags=['assets_heavy_risk'], stars=75)
- `SmashPhil/Vehicle-Framework` (cat=library/sdk, flags=['assets_heavy_risk'], stars=78)

## Known limitations (metadata-only)

- **Language share unverified**: GitHub `size` includes all files/history; we cannot confirm C# is the majority language or that assets/binaries do not dominate. Verify at ingestion (file discovery stage already measures bytes by extension).
- **Fork/archived status unknown**: not present in the data, so `fork_of` is always null and no archived flag exists. Forks and archived repos must be detected at clone time (GitHub API) and merged into groups before caret expansion.
- **Real license unverified**: the SPDX id is GitHub's best guess; the actual LICENSE file and per-file headers must be confirmed before any repo enters the training corpus. NOASSERTION/unknown repos are quarantined, not trusted.
- **Generated code**: `.g.cs`/`.Designer.cs`/migrations cannot be seen from metadata; excluded later by the extraction file filters.
- **Dedup is conservative**: only identical/high-overlap descriptions are merged. Genuine forks with divergent descriptions (e.g. the several space-station-14 builds) stay in separate groups and must be regrouped by fork lineage at ingestion.
