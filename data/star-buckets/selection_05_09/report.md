# C# repository selection report

Metadata-only selection over `data/csharp-search.jsonl`. No repository was cloned, fetched, or executed; descriptions were treated as untrusted data and only pattern-matched. Reproduce with `python3 -I data/selection/selection_script.py`.

## Funnel

- Input rows: **9753**
- After exact-duplicate-row dedup: **9753** unique `full_name` (0 duplicate rows removed; search-pagination overlap)
- Selected: **9540** (in 9540 dedup groups)
- Rejected: **213**
  - license_unknown bucket: **1**
  - restricted_license bucket: **0**

## Rejection reasons (a repo may carry several)

| reason | count |
| --- | ---: |
| owner_cap | 91 |
| tiny_lowsignal | 67 |
| abuse_keyword | 24 |
| duplicate_of_group | 14 |
| abuse_topic | 12 |
| noncode_or_list | 5 |
| license_unknown | 1 |
| gibberish_lowsignal | 1 |

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
| other | 3783 |
| library/sdk | 1060 |
| game/unity | 995 |
| desktop-tool | 952 |
| desktop-ui | 592 |
| web/aspnet | 479 |
| ml/ai | 465 |
| data/db/orm | 284 |
| cloud/azure | 263 |
| devtools | 225 |
| networking | 178 |
| security | 138 |
| testing | 80 |
| iot/embedded | 46 |

## Selection — license distribution

| license | count |
| --- | ---: |
| MIT | 8169 |
| Apache-2.0 | 1059 |
| BSD-3-Clause | 128 |
| Unlicense | 120 |
| BSD-2-Clause | 26 |
| MS-PL | 14 |
| ISC | 12 |
| Zlib | 8 |
| 0BSD | 4 |

## Selection — top owners (after cap)

| owner | count |
| --- | ---: |
| nanoframework | 15 |
| microsoft | 15 |
| meshmakers | 15 |
| damienbod | 15 |
| elbruno | 15 |
| renatogroffe | 15 |
| isairey | 15 |
| devlinj678 | 14 |
| jchristn | 13 |
| smourier | 13 |
| SeppPenner | 13 |
| VerifyTests | 12 |
| NewLifeX | 12 |
| marcominerva | 12 |
| rossogames | 12 |
| SkillsFundingAgency | 11 |
| madskristensen | 11 |
| cake-contrib | 10 |
| ricaun-io | 10 |
| Azure-Samples | 9 |

## Selection — quality flags

| flag | count |
| --- | ---: |
| tiny | 3874 |
| no_desc_no_topics | 1101 |
| educational | 181 |
| assets_heavy_risk | 64 |
| large | 48 |
| throwaway_owner_suspect | 6 |

## Borderline cases (kept, flagged for later review)

- `froglet-studio/Cosmic-Shore` (cat=other, flags=['large', 'assets_heavy_risk'], stars=9)
- `Bellseboss-Studio/MortalKombatProjectOC` (cat=game/unity, flags=['large', 'assets_heavy_risk'], stars=5)
- `RimworldCosmere/RimworldCosmere` (cat=other, flags=['assets_heavy_risk'], stars=8)
- `Kinds-of-Intelligence-CFI/animal-ai-unity` (cat=game/unity, flags=['assets_heavy_risk'], stars=5)
- `TigersUniverse/Hypernex.Unity` (cat=game/unity, flags=['assets_heavy_risk'], stars=8)
- `haterade22/TAOM` (cat=ml/ai, flags=['large', 'assets_heavy_risk'], stars=8)
- `AbstractOcclusion-gif/unity6-webgpu-interactive-water` (cat=game/unity, flags=['assets_heavy_risk'], stars=7)
- `TheWizardsCode/RogueWave` (cat=game/unity, flags=['large', 'assets_heavy_risk'], stars=6)
- `evangelosvlachos96-dotcom/ceco-chat` (cat=cloud/azure, flags=['throwaway_owner_suspect'], stars=5)
- `evangelosvlachos96-dotcom/clean-architecture-template` (cat=web/aspnet, flags=['throwaway_owner_suspect', 'tiny'], stars=5)
- `evangelosvlachos96-dotcom/ecommerce-api-dotnet` (cat=web/aspnet, flags=['throwaway_owner_suspect'], stars=5)
- `aerospike-community/aerospike-linqpad-driver` (cat=other, flags=['assets_heavy_risk'], stars=6)

## Known limitations (metadata-only)

- **Language share unverified**: GitHub `size` includes all files/history; we cannot confirm C# is the majority language or that assets/binaries do not dominate. Verify at ingestion (file discovery stage already measures bytes by extension).
- **Fork/archived status unknown**: not present in the data, so `fork_of` is always null and no archived flag exists. Forks and archived repos must be detected at clone time (GitHub API) and merged into groups before caret expansion.
- **Real license unverified**: the SPDX id is GitHub's best guess; the actual LICENSE file and per-file headers must be confirmed before any repo enters the training corpus. NOASSERTION/unknown repos are quarantined, not trusted.
- **Generated code**: `.g.cs`/`.Designer.cs`/migrations cannot be seen from metadata; excluded later by the extraction file filters.
- **Dedup is conservative**: only identical/high-overlap descriptions are merged. Genuine forks with divergent descriptions (e.g. the several space-station-14 builds) stay in separate groups and must be regrouped by fork lineage at ingestion.
