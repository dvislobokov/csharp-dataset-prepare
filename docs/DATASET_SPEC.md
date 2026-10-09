# Dataset specification (v1)

All outputs of one run live in one directory, written atomically (`<out>.tmp-<pid>` → rename; `*.partial` while a shard is open).

| File | Schema | One record per |
|---|---|---|
| `discovery.jsonl` | `discovery-file/v1` (no JSON schema yet; see `DiscoveryRecord`) | every candidate `.cs` path, accepted or skipped with `skip_reason` |
| `corpus.jsonl` | [`schemas/corpus-file.v1.schema.json`](../schemas/corpus-file.v1.schema.json) | accepted source file (product A) |
| `samples.jsonl` | [`schemas/flc-sample.v1.schema.json`](../schemas/flc-sample.v1.schema.json) | caret sample (product B) |
| `semantic.jsonl` | [`schemas/flc-semantic.v1.schema.json`](../schemas/flc-semantic.v1.schema.json) | (sample, visibility policy) — semantic modes only |
| `exclusions.jsonl` | `flc-exclusion/v1` | audit examples of excluded carets (capped per reason; full counts in `summary.json`) |
| `summary.json` | `RunSummary` | deterministic content counters (byte-identical across reruns) |
| `run-manifest.json` | [`schemas/run-manifest.v1.schema.json`](../schemas/run-manifest.v1.schema.json) | provenance, config, environment, timings, resources, output checksums |
| `run.log.jsonl` | structured log | event |
| `validation.json` | written by `validate` | check counts and failures |

`--gzip` writes `*.jsonl.gz` instead; readers accept both.

## Coordinates and text

* `*_utf16_offset`: UTF-16 code units into the **decoded** file text, BOM removed (Roslyn positions).
* `*_byte_offset`: bytes into the **original** file, BOM included. `source_sha256` hashes those original bytes.
* Original bytes = `(has_bom ? EF BB BF : "") + UTF8(content)`; files that are not strict UTF-8 (or are UTF-16 / contain NUL) are skipped with a reason.
* Lines/columns are zero-based; columns count UTF-16 units. Line breaks follow Roslyn: CRLF, CR, LF, U+0085, U+2028, U+2029.

## Sample invariants (checked by `validate` on every sample against the repository bytes)

```
text = decode(file)
text[caret .. target_end] == target_text
text == text[..caret] + target_text + text[target_end..]
left_context  == text[left_context_start .. caret]          (≤ context.left_chars; never splits a surrogate pair)
right_context == text[target_end .. right_context_end]       (starts with trailing whitespace, then the line break)
target_text has no line break, is non-empty, does not end with whitespace
text[target_end .. line_end] is whitespace only
```

**Whitespace convention.** Indentation before a `line_start` caret is considered already typed (the editor auto-indents).
After keywords/commas/operators the caret is placed after the typed space. Trailing whitespace of a line never belongs to
the target (it is the start of `right_context`; flagged `trailing_whitespace`). `token_boundary` carets may produce targets
starting with a space; those carry `target_starts_with_whitespace`.

**Sample id** = `sha256(repository_id ␟ relative_path ␟ source_sha256 ␟ caret ␟ target_end)[:32]` — content-derived, so the
same file content produces the same ids at any revision, worker count or run.

**Splits** are assigned to a *group* before caret expansion (`split.group_by`: `project_family` | `project` | `file`). The
pilot groups by project family (`Ordering.API`, `Ordering.Domain`, `Ordering.UnitTests` → `Ordering`) and pins eval families in
`split.overrides`. `validate` fails if one group appears in two splits.

## Quality flags

`trivial_target` (only `{}()[];,`), `target_starts_with_whitespace`, `target_starts_with_closer` (`)`/`]`/`}` first — the editor
usually auto-inserted it), `trailing_whitespace`, `target_has_comment`, `file_has_syntax_errors`, `non_ascii_target`.

## Semantic sidecar

Keyed by `(sample_id, visibility_policy)`. Status ∈ `resolved | partially_resolved | syntax_fallback | failed` with a reason code
(`document_not_in_workspace`, `document_text_mismatch`, `project_load`, `unresolved_receiver_type`, `no_invocation_candidates`,
`no_enclosing_symbol`, `timeout`, `exception:<Type>`). The sample's own `semantic_status/semantic_reason` mirror the
`editor_snapshot` result (or `not_attempted` + `not_in_subset`). Facts:

* `locals`, `parameters` (incl. `primary_ctor_parameter`, `lambda_parameter`), `this_members` — usable at the caret.
* `receiver_type`/`receiver_kind`/`members` after `.`/`?.`/qualified names; accessible members only, overloads grouped.
* `invocation_candidates` inside `(`/`,` of a call: every overload applicable to the argument index, never a single guess.
* `expected_type` + `expected_type_source` only when bound (argument with a single parameter type, `return`, assignment,
  typed initializer, expression body, condition).
* Type names are minimal display strings valid at the caret line; identifiers are escaped (`@event`).
* `nullable_annotation` is the **declared** annotation (for `var`, the initializer's type). Flow state is not emitted.
* `leakage`: target identifiers, the ones the payload mentions (coverage — legitimate when the symbol exists independently),
  and `violations` (must be empty; enforced by schema and `validate`).
* `synthetic_suffix` (strict_prefix only), `dropped_recovery_artifacts`, `truncated`, `prompt` (compact debug rendering).

## Training serialization

Model-facing records are produced by a separate, versioned step (`render`), see [PROMPT_FORMAT.md](PROMPT_FORMAT.md).
Canonical records never contain a preformatted prompt.
