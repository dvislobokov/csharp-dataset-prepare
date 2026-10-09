# C# FLC dataset builder — working notes for Claude Code

The specification and the implementation decisions are in `AGENTS.md` (read it first); this file holds how to work here
and the current state. Sister project for Go: https://github.com/dvislobokov/go-dataset-prepare (same pipeline, `goflc`).

## Communication and conventions
- Answer the user in Russian; code, identifiers, comments, docs and commit messages in English.
- Commit as the GitHub no-reply identity (`196775686+dvislobokov@users.noreply.github.com`), never the personal e-mail of the
  global git config; end messages with the Co-Authored-By / Claude-Session lines. Push only when the user asks.
- Never print or commit tokens: `~/HF_TOKEN`, `~/GITHUB_TOKEN` locally, `/srv/flc/secrets/{HF_TOKEN,GITHUB_TOKEN}` (600, owner flc)
  on the server. Git gets the GitHub token through env (`GIT_CONFIG_*`), never in URLs or argv.
- Analysed code is untrusted: the bulk run uses the `adhoc` semantic tier only (no restore, MSBuild or code execution).
- Never edit a shell script while it runs (bash reads it incrementally); rsync replaces files atomically, so deploying is safe.

## Data products (Hugging Face, public: https://huggingface.co/datasets/dvislobokov/csharp-ml-complation)
- Configs `samples` (flc-sample/v1), `semantic` (Roslyn facts on the target-free snapshot, leak-audited), `prompts` (flc-prompt/v2
  pairs), `corpus` (whole files), `repos` (provenance/status). Every row carries repository, revision, path and licence.
- Shard names `<split>-<tag>-<batch>.parquet`: `r1`/`c1` = main run (4 322 repos, ≥81 stars), `s35_39` … `s75_80` = star buckets
  (3 866 repos; 0..34 excluded by the user, `data/star-buckets/`).
- `engine/cs-16384/`: the corpus encoded with the plugin engine's tokenizer `cs-16384.bpe` (idea-ml-completion, pre-tokenizer
  go-code-1) in its training-shard format: `lm` 3.18 G tokens / 7 811 repos, `validation` 63 M, `test` 64 M. Its
  `tools/nn/train/train.py --data <dir> --vocab <dir>/cs-16384.bpe` reads it unchanged. Rebuild: `scripts/encode_engine_shards.py`.
- Tokenizer decision (docs/TOKENIZER.md): the model for the plugin uses the engine tokenizer; HF tokenizers were a study only.
- Splits are per repository group (`flc_run.py split_of`), NOT the engine's md5 folds: our test repositories may be in the
  engine's own training corpus (≥20 stars) — evaluate existing plugin models on fresh repositories, not on our test split.

## Server 161.104.53.71 (Ubuntu, 128 vCPU, 251 GB, 1 TB, no GPU; `ssh root@…`, jobs run as user `flc`)
- `/srv/flc/app` (this repo, deployed with rsync), `/srv/flc/venv` (pipeline Python), `/srv/flc/venv-tok` (tokenizers; keep separate:
  installing `tokenizers` into the pipeline venv downgraded `huggingface_hub` once), `/opt/dotnet` (.NET 10).
- State: `/srv/flc/state/jobs.sqlite` (samples pass), `/srv/flc/corpus/state.sqlite` (corpus pass); logs `/srv/flc/logs`,
  `/srv/flc/corpus/stdout.log`. Star-bucket search/selection: `/srv/flc/search`. Engine shards: `/srv/flc/engine-shards`.
- Orchestrator: `scripts/flc_run.py --manifest … --jobs N --workers 2 [--corpus-only]`; SIGTERM stops gracefully (running jobs go back
  to pending); restart with the same command resumes and uploads what is packed. Record `run_tag` in a manifest row to tag shards.
- Lessons: ~3× CPU oversubscription made SQLite writers time out (fixed: short write transactions, 600 s busy timeout, retries;
  uploader survives a locked DB); `renice` of a thread is inherited by the processes it spawns.

## State (2026-10-09) and next steps
- Done: main + star-bucket runs uploaded (8 130 repos with samples, 8 143 in the corpus), engine shards published, tokenizer study.
- Context block spec (draft, for the user to review): `docs/CONTEXT_SPEC-RU.md` (`flc-context/v1`).
- Next (agreed plan): (1) per-repository dependency profile `DEPS` (external `using` namespaces in ≥2 other files of the repo);
  (2) spec of the context block for the plugin (facts `RET/ARG/LOCAL/RECV/MEMBER/CALL/TYPE` right before the current line via
  `<|reserved_N|>` specials, `DEPS` at the start; golden examples); (3) generator of training documents in the engine format with
  and without context, encoded with `cs-16384.bpe`; (4) `eval_inline` with/without context on the same positions; (5) proxy model
  (31 M) on a rented H200 before any plugin work — the engine's earlier Roslyn context study gained only ~2 %.
