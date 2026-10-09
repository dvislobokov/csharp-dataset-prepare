# C# repository selection report

Metadata-only selection over `data/csharp-search.jsonl`. No repository was cloned, fetched, or executed; descriptions were treated as untrusted data and only pattern-matched. Reproduce with `python3 -I data/selection/selection_script.py`.

## Funnel

- Input rows: **7253**
- After exact-duplicate-row dedup: **7184** unique `full_name` (69 duplicate rows removed; search-pagination overlap)
- Selected: **4376** (in 4376 dedup groups)
- Rejected: **2808**
  - license_unknown bucket: **1515**
  - restricted_license bucket: **1041**

## Rejection reasons (a repo may carry several)

| reason | count |
| --- | ---: |
| license_unknown | 1515 |
| restricted_license | 1041 |
| owner_cap | 194 |
| abuse_keyword | 28 |
| abuse_topic | 27 |
| duplicate_of_group | 14 |
| noncode_or_list | 14 |
| manual_exclude | 7 |
| tiny_lowsignal | 3 |
| huge_asset_dump | 2 |
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
| other | 1037 |
| game/unity | 683 |
| library/sdk | 523 |
| desktop-tool | 475 |
| desktop-ui | 447 |
| web/aspnet | 345 |
| devtools | 187 |
| data/db/orm | 176 |
| cloud/azure | 146 |
| networking | 122 |
| ml/ai | 121 |
| security | 62 |
| testing | 46 |
| iot/embedded | 6 |

## Selection — license distribution

| license | count |
| --- | ---: |
| MIT | 3451 |
| Apache-2.0 | 708 |
| BSD-3-Clause | 102 |
| Unlicense | 40 |
| BSD-2-Clause | 38 |
| MS-PL | 20 |
| Zlib | 15 |
| 0BSD | 2 |

## Selection — top owners (after cap)

| owner | count |
| --- | ---: |
| microsoft | 15 |
| dotnet | 15 |
| Cysharp | 15 |
| dotnetcore | 15 |
| Azure | 15 |
| Azure-Samples | 15 |
| oculus-samples | 15 |
| yasirkula | 15 |
| keijiro | 15 |
| EricZimmerman | 13 |
| serilog | 12 |
| xoofx | 11 |
| NewLifeX | 10 |
| aws | 10 |
| Tyrrrz | 10 |
| wieslawsoltes | 10 |
| SimonCropp | 10 |
| AvaloniaUI | 9 |
| reactiveui | 9 |
| Fody | 9 |

## Selection — quality flags

| flag | count |
| --- | ---: |
| tiny | 581 |
| no_desc_no_topics | 113 |
| assets_heavy_risk | 80 |
| educational | 76 |
| large | 47 |
| throwaway_owner_suspect | 2 |

## Borderline cases (kept, flagged for later review)

- `tModLoader/tModLoader` (cat=library/sdk, flags=['assets_heavy_risk'], stars=5709)
- `MirrorNetworking/Mirror` (cat=game/unity, flags=['assets_heavy_risk'], stars=6357)
- `Cysharp/MagicOnion` (cat=game/unity, flags=['assets_heavy_risk'], stars=4453)
- `LavaGang/MelonLoader` (cat=game/unity, flags=['assets_heavy_risk'], stars=4241)
- `microsoft/MixedRealityToolkit-Unity` (cat=game/unity, flags=['large', 'assets_heavy_risk'], stars=6077)
- `ppy/osu-framework` (cat=game/unity, flags=['assets_heavy_risk'], stars=2018)
- `icosa-foundation/open-brush` (cat=game/unity, flags=['large', 'assets_heavy_risk'], stars=1135)
- `AnyRPG/AnyRPGCore` (cat=game/unity, flags=['assets_heavy_risk'], stars=1017)
- `DevTeam/Pure.DI` (cat=devtools, flags=['assets_heavy_risk'], stars=847)
- `IviriusCommunity/Rebound` (cat=desktop-ui, flags=['assets_heavy_risk'], stars=861)
- `UltraStar-Deluxe/Play` (cat=game/unity, flags=['large', 'assets_heavy_risk'], stars=508)
- `Cysharp/YetAnotherHttpHandler` (cat=game/unity, flags=['assets_heavy_risk'], stars=512)

## Known limitations (metadata-only)

- **Language share unverified**: GitHub `size` includes all files/history; we cannot confirm C# is the majority language or that assets/binaries do not dominate. Verify at ingestion (file discovery stage already measures bytes by extension).
- **Fork/archived status unknown**: not present in the data, so `fork_of` is always null and no archived flag exists. Forks and archived repos must be detected at clone time (GitHub API) and merged into groups before caret expansion.
- **Real license unverified**: the SPDX id is GitHub's best guess; the actual LICENSE file and per-file headers must be confirmed before any repo enters the training corpus. NOASSERTION/unknown repos are quarantined, not trusted.
- **Generated code**: `.g.cs`/`.Designer.cs`/migrations cannot be seen from metadata; excluded later by the extraction file filters.
- **Dedup is conservative**: only identical/high-overlap descriptions are merged. Genuine forks with divergent descriptions (e.g. the several space-station-14 builds) stay in separate groups and must be regrouped by fork lineage at ingestion.
