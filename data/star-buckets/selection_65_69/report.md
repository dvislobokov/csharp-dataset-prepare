# C# repository selection report

Metadata-only selection over `data/csharp-search.jsonl`. No repository was cloned, fetched, or executed; descriptions were treated as untrusted data and only pattern-matched. Reproduce with `python3 -I data/selection/selection_script.py`.

## Funnel

- Input rows: **284**
- After exact-duplicate-row dedup: **284** unique `full_name` (0 duplicate rows removed; search-pagination overlap)
- Selected: **280** (in 280 dedup groups)
- Rejected: **4**
  - license_unknown bucket: **0**
  - restricted_license bucket: **0**

## Rejection reasons (a repo may carry several)

| reason | count |
| --- | ---: |
| noncode_or_list | 2 |
| abuse_keyword | 2 |

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
| other | 84 |
| game/unity | 42 |
| library/sdk | 41 |
| desktop-tool | 25 |
| web/aspnet | 21 |
| desktop-ui | 18 |
| devtools | 12 |
| data/db/orm | 11 |
| networking | 9 |
| cloud/azure | 8 |
| ml/ai | 4 |
| testing | 2 |
| security | 2 |
| iot/embedded | 1 |

## Selection — license distribution

| license | count |
| --- | ---: |
| MIT | 235 |
| Apache-2.0 | 31 |
| BSD-2-Clause | 6 |
| BSD-3-Clause | 4 |
| Unlicense | 2 |
| 0BSD | 1 |
| Zlib | 1 |

## Selection — top owners (after cap)

| owner | count |
| --- | ---: |
| microsoft | 4 |
| chickensoft-games | 2 |
| octokit | 2 |
| loresoft | 2 |
| Azure | 2 |
| Azure-Samples | 2 |
| SvenGDK | 2 |
| dotnet-campus | 2 |
| julienkay | 2 |
| JamesnetGroup | 2 |
| VRLabs | 2 |
| bimwright | 1 |
| gamesmiths-guild | 1 |
| FoundatioFx | 1 |
| Nimblesite | 1 |
| Wissance | 1 |
| HotcakesCommerce | 1 |
| context-and-oss | 1 |
| abstracta | 1 |
| Crazy-Marvin | 1 |

## Selection — quality flags

| flag | count |
| --- | ---: |
| tiny | 58 |
| no_desc_no_topics | 12 |
| assets_heavy_risk | 4 |
| educational | 2 |
| large | 1 |

## Borderline cases (kept, flagged for later review)

- `Crazy-Marvin/EllaTheGame` (cat=game/unity, flags=['assets_heavy_risk'], stars=67)
- `EOS-Contrib/eos_plugin_for_unity_upm` (cat=other, flags=['assets_heavy_risk'], stars=68)
- `Adsito/RustMapEditor` (cat=game/unity, flags=['assets_heavy_risk'], stars=67)
- `irontree2022/1msRenderVegetation` (cat=other, flags=['large', 'assets_heavy_risk'], stars=66)

## Known limitations (metadata-only)

- **Language share unverified**: GitHub `size` includes all files/history; we cannot confirm C# is the majority language or that assets/binaries do not dominate. Verify at ingestion (file discovery stage already measures bytes by extension).
- **Fork/archived status unknown**: not present in the data, so `fork_of` is always null and no archived flag exists. Forks and archived repos must be detected at clone time (GitHub API) and merged into groups before caret expansion.
- **Real license unverified**: the SPDX id is GitHub's best guess; the actual LICENSE file and per-file headers must be confirmed before any repo enters the training corpus. NOASSERTION/unknown repos are quarantined, not trusted.
- **Generated code**: `.g.cs`/`.Designer.cs`/migrations cannot be seen from metadata; excluded later by the extraction file filters.
- **Dedup is conservative**: only identical/high-overlap descriptions are merged. Genuine forks with divergent descriptions (e.g. the several space-station-14 builds) stay in separate groups and must be regrouped by fork lineage at ingestion.
