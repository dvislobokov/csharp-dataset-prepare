# C# repository selection report

Metadata-only selection over `data/csharp-search.jsonl`. No repository was cloned, fetched, or executed; descriptions were treated as untrusted data and only pattern-matched. Reproduce with `python3 -I data/selection/selection_script.py`.

## Funnel

- Input rows: **3878**
- After exact-duplicate-row dedup: **3878** unique `full_name` (0 duplicate rows removed; search-pagination overlap)
- Selected: **3836** (in 3836 dedup groups)
- Rejected: **42**
  - license_unknown bucket: **0**
  - restricted_license bucket: **0**

## Rejection reasons (a repo may carry several)

| reason | count |
| --- | ---: |
| tiny_lowsignal | 18 |
| abuse_keyword | 16 |
| abuse_topic | 4 |
| noncode_or_list | 3 |
| duplicate_of_group | 1 |

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
| other | 1410 |
| library/sdk | 453 |
| game/unity | 438 |
| desktop-tool | 403 |
| desktop-ui | 257 |
| web/aspnet | 193 |
| ml/ai | 133 |
| cloud/azure | 132 |
| data/db/orm | 120 |
| devtools | 108 |
| networking | 94 |
| security | 43 |
| testing | 37 |
| iot/embedded | 15 |

## Selection — license distribution

| license | count |
| --- | ---: |
| MIT | 3226 |
| Apache-2.0 | 485 |
| BSD-3-Clause | 46 |
| Unlicense | 44 |
| BSD-2-Clause | 13 |
| MS-PL | 8 |
| Zlib | 8 |
| 0BSD | 3 |
| ISC | 3 |

## Selection — top owners (after cap)

| owner | count |
| --- | ---: |
| microsoft | 13 |
| renatogroffe | 13 |
| nanoframework | 10 |
| Azure-Samples | 10 |
| devlooped | 9 |
| damienbod | 8 |
| madskristensen | 8 |
| Chendaqian | 8 |
| jchristn | 7 |
| LostBeard | 6 |
| smourier | 6 |
| VerifyTests | 5 |
| elbruno | 5 |
| KillzXGaming | 5 |
| phongnguyend | 5 |
| cafeconleche7897 | 5 |
| nano-byte | 4 |
| Azure | 4 |
| aws | 4 |
| space-wizards | 4 |

## Selection — quality flags

| flag | count |
| --- | ---: |
| tiny | 1413 |
| no_desc_no_topics | 356 |
| educational | 69 |
| assets_heavy_risk | 29 |
| large | 18 |

## Borderline cases (kept, flagged for later review)

- `PisterLab/micromissiles-unity` (cat=game/unity, flags=['assets_heavy_risk'], stars=11)
- `pwri-opera/OperaSim-AGX` (cat=game/unity, flags=['assets_heavy_risk'], stars=11)
- `skooter500/Forms` (cat=game/unity, flags=['assets_heavy_risk'], stars=14)
- `nickc01/WeaverCore` (cat=game/unity, flags=['assets_heavy_risk'], stars=13)
- `jpw1991/chebs-necromancy` (cat=other, flags=['assets_heavy_risk'], stars=13)
- `bigibas123/avatar-goodies` (cat=game/unity, flags=['assets_heavy_risk'], stars=14)
- `WVCSergkart/WVC_RacesBiotech` (cat=other, flags=['assets_heavy_risk'], stars=10)
- `Chilly5/OpenEmpires` (cat=game/unity, flags=['large', 'assets_heavy_risk'], stars=11)
- `Leanplum/Leanplum-Unity-SDK` (cat=game/unity, flags=['assets_heavy_risk'], stars=12)
- `32Kallies/SealSub` (cat=other, flags=['assets_heavy_risk'], stars=12)
- `Zen-Van/CMGM_UnityFrame` (cat=other, flags=['assets_heavy_risk'], stars=14)
- `Josh-Jun/UnityAppFramework` (cat=game/unity, flags=['assets_heavy_risk'], stars=10)

## Known limitations (metadata-only)

- **Language share unverified**: GitHub `size` includes all files/history; we cannot confirm C# is the majority language or that assets/binaries do not dominate. Verify at ingestion (file discovery stage already measures bytes by extension).
- **Fork/archived status unknown**: not present in the data, so `fork_of` is always null and no archived flag exists. Forks and archived repos must be detected at clone time (GitHub API) and merged into groups before caret expansion.
- **Real license unverified**: the SPDX id is GitHub's best guess; the actual LICENSE file and per-file headers must be confirmed before any repo enters the training corpus. NOASSERTION/unknown repos are quarantined, not trusted.
- **Generated code**: `.g.cs`/`.Designer.cs`/migrations cannot be seen from metadata; excluded later by the extraction file filters.
- **Dedup is conservative**: only identical/high-overlap descriptions are merged. Genuine forks with divergent descriptions (e.g. the several space-station-14 builds) stay in separate groups and must be regrouped by fork lineage at ingestion.
