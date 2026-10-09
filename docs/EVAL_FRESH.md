# Clean evaluation set `eval-fresh` (C# and Go, 5..19 stars)

## Definition
A held-out evaluation set of repositories that neither the plugin engine's corpus (GitHub repos with >= 20 stars) nor our
own training data (C# >= 35 stars, Go >= 100 stars) can contain: public repositories with **5..19 stars**, drawn evenly from the
buckets 5..9, 10..14, 15..19. 100 repositories per bucket and language (300 C# + 300 Go). Same extraction/prompt pipeline as the
main datasets (`flc-sample/v1`, `flc-semantic/v1`, `flc-prompt/v2`, `corpus-file/v1`), run tag `fresh`.

## Why it is clean
- Star ranges are disjoint from every training source (the engine's threshold is 20 stars).
- The draw excludes every `repo_id` present in the training state DBs (`/srv/flc/state/jobs.sqlite`,
  `/srv/flc/corpus/state.sqlite`, `/srv/flc-go/state/jobs.sqlite`, `/srv/flc-go/corpus/state.sqlite`; 8 242 C# and 8 951 Go ids); none was found.
- Caveat: stars are a snapshot (2026-10-09). A repository can have had a different star count when a model corpus was built, and
  forks/copies of popular code can exist inside 5..19-star repositories (near-duplicate contamination is not measured here).

## Selection procedure
1. Search: `scripts/gh_search.py` (new `--language`, default `C#`; Go runs used `--language Go`), not fork, not archived,
   pushed since 2024-10-09, permissive licence allowlist, one run per bucket. Go outputs: `/srv/flc/search-go/stars_{05_09,10_14,15_19}.jsonl`.
2. Selection (same rules as the training runs): C# `data/selection/selection_script.py`
   (existing outputs `/srv/flc/search/selection_*/selected.jsonl.excluded`), Go `data/selection/selection_script.py` of go-dataset
   (now takes `<input> <outdir>`; outputs `/srv/flc/search-go/selection_*/selected.jsonl`).
   Selected pool sizes: C# 9 540 / 3 836 / 2 170, Go 13 545 / 5 669 / 3 261 (5..9 / 10..14 / 15..19).
3. Draw: `scripts/eval_fresh_sample.py`, seed `eval-fresh-v1`: per bucket, order by `sha256("eval-fresh-v1:" + repository_id)` and take
   the first 100 (after dropping ids found in the training state DBs). Each manifest row gets `"run_tag": "fresh"`.
   Manifests: `/srv/flc/eval-fresh/manifest.jsonl`, `/srv/flc-go/eval-fresh/manifest.jsonl`.
4. Processing: existing orchestrators (`flc_run.py`, `goflc_run.py`), separate state/work/out/logs under `eval-fresh/`,
   `--jobs 12 --workers 2 --path-prefix eval-fresh --run-tag fresh`; one samples pass and one `--corpus-only` pass per language.

## Counts
| | C# | Go |
| --- | --- | --- |
| manifest repos | 300 | 300 |
| uploaded (samples pass) | 298 | 297 |
| skipped | 2 (`no_cs_files`, `no_samples`) | 2 (`license_not_allowed`, `no_go_files`) |
| failed | 0 | 1 (`validation:target_newline_or_empty`, `nicolasbonnici/gorest`; not retried or fixed) |
| samples (rows) | 1 416 620 | 1 404 263 |
| samples by bucket 5..9 / 10..14 / 15..19 | 468 242 / 456 821 / 491 557 | 450 130 / 423 577 / 530 556 |
| repos by split train / validation / test | 287 / 6 / 7 | 287 / 8 / 5 |
| corpus pass: uploaded repos | 299 (1 skipped) | 298 (2 skipped) |

Splits are assigned by the orchestrators per repository group; the whole set is one evaluation set, so use all splits together.
Samples are capped per repository by the pipeline (up to 20 000), so large repositories do not dominate.

## Hugging Face paths
- `dvislobokov/csharp-ml-complation` under `eval-fresh/`: `data/{samples,semantic,prompts}/<split>-fresh-<batch>.parquet`
  (15 files each), `data/repos/` (7), `data/corpus/` (3), `README.md`, `LICENSE.md`.
- `dvislobokov/go-ml-complation` under `eval-fresh/`: same layout (13 files per samples/semantic/prompts config, 7 repos, 3 corpus).
- The root README and root `data/` were not touched.
- The corpus pass provides full file text, needed later to build prompts with longer suffixes.

## Caveats
- Lower average quality than the training data: many 5..19-star repositories are small tutorials, forks-in-spirit, coursework, generated
  or copied code; results on this set are expected to differ from those on popular repositories and are not directly comparable.
- Licence per row is carried in the dataset (only permissive licences were searched).
- Star counts are a snapshot taken at search time; repositories were pinned to the HEAD at processing time (revision per row).
