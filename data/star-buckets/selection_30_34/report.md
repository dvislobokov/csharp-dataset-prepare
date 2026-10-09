# C# repository selection report

Metadata-only selection over `data/csharp-search.jsonl`. No repository was cloned, fetched, or executed; descriptions were treated as untrusted data and only pattern-matched. Reproduce with `python3 -I data/selection/selection_script.py`.

## Funnel

- Input rows: **910**
- After exact-duplicate-row dedup: **910** unique `full_name` (0 duplicate rows removed; search-pagination overlap)
- Selected: **902** (in 902 dedup groups)
- Rejected: **8**
  - license_unknown bucket: **0**
  - restricted_license bucket: **0**

## Rejection reasons (a repo may carry several)

| reason | count |
| --- | ---: |
| noncode_or_list | 3 |
| tiny_lowsignal | 3 |
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
| other | 270 |
| game/unity | 136 |
| library/sdk | 118 |
| desktop-tool | 90 |
| desktop-ui | 82 |
| web/aspnet | 53 |
| ml/ai | 34 |
| devtools | 31 |
| cloud/azure | 30 |
| data/db/orm | 23 |
| networking | 18 |
| security | 7 |
| iot/embedded | 5 |
| testing | 5 |

## Selection — license distribution

| license | count |
| --- | ---: |
| MIT | 759 |
| Apache-2.0 | 110 |
| BSD-3-Clause | 13 |
| Unlicense | 10 |
| BSD-2-Clause | 6 |
| 0BSD | 4 |

## Selection — top owners (after cap)

| owner | count |
| --- | ---: |
| microsoft | 7 |
| Azure-Samples | 5 |
| codewriter-packages | 3 |
| dotnet | 3 |
| NewLifeX | 3 |
| wieslawsoltes | 3 |
| EasyAbp | 3 |
| Lacro59 | 3 |
| damienbod | 3 |
| tomlm | 3 |
| keijiro | 3 |
| JamesnetGroup | 3 |
| cake-contrib | 2 |
| USACE-RMC | 2 |
| siemens | 2 |
| Singulink | 2 |
| akkadotnet | 2 |
| devlooped | 2 |
| serilog-contrib | 2 |
| AssetRipper | 2 |

## Selection — quality flags

| flag | count |
| --- | ---: |
| tiny | 250 |
| no_desc_no_topics | 71 |
| educational | 10 |
| assets_heavy_risk | 8 |
| large | 4 |

## Borderline cases (kept, flagged for later review)

- `tenjin/tenjin-unity-sdk` (cat=game/unity, flags=['assets_heavy_risk'], stars=34)
- `chocowriter/unity6-division-like` (cat=game/unity, flags=['large', 'assets_heavy_risk'], stars=33)
- `gold-meridian/terraria-unified` (cat=other, flags=['assets_heavy_risk'], stars=30)
- `ayutaz/uPiper` (cat=game/unity, flags=['assets_heavy_risk'], stars=33)
- `joanby/cursos-unity-5` (cat=game/unity, flags=['assets_heavy_risk'], stars=30)
- `cheesestudio/VRChat-Pool-table-15-red-snooker-Pyramid-Chinese-8-MS-VRCSA-Billiards` (cat=game/unity, flags=['assets_heavy_risk'], stars=31)
- `dojoengine/dojo.unity` (cat=game/unity, flags=['assets_heavy_risk'], stars=31)
- `hearth1an/hearthian.TheVision` (cat=other, flags=['assets_heavy_risk'], stars=31)

## Known limitations (metadata-only)

- **Language share unverified**: GitHub `size` includes all files/history; we cannot confirm C# is the majority language or that assets/binaries do not dominate. Verify at ingestion (file discovery stage already measures bytes by extension).
- **Fork/archived status unknown**: not present in the data, so `fork_of` is always null and no archived flag exists. Forks and archived repos must be detected at clone time (GitHub API) and merged into groups before caret expansion.
- **Real license unverified**: the SPDX id is GitHub's best guess; the actual LICENSE file and per-file headers must be confirmed before any repo enters the training corpus. NOASSERTION/unknown repos are quarantined, not trusted.
- **Generated code**: `.g.cs`/`.Designer.cs`/migrations cannot be seen from metadata; excluded later by the extraction file filters.
- **Dedup is conservative**: only identical/high-overlap descriptions are merged. Genuine forks with divergent descriptions (e.g. the several space-station-14 builds) stay in separate groups and must be regrouped by fork lineage at ingestion.
