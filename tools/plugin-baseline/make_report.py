#!/usr/bin/env python3
"""make_report.py <scratch_dir_with_results> <out_dir> -> PLUGIN_BASELINE.json + .md"""
import json,sys,re,glob,os
S,O=sys.argv[1],sys.argv[2]
acc=json.load(open(f"{S}/acc-test.json"))
models=list(acc)
lat={m:json.load(open(f"{S}/lat-{m}.json")) for m in models}
bench={}
for m in models:
    t=open(f"{S}/nnbench-{m}.log").read(); line=[l for l in t.splitlines() if l.startswith("RESULT")][0]
    bench[m]=dict(kv.split("=",1) for kv in line.split("\t")[1:])
meta={l:json.load(open(f"{S}/cases-{l}.tsv.meta.json")) for l in ("cs","go")}
json.dump({"accuracy_test_split":acc,"latency_8_threads":lat,"nnbench_random_prompt_1500":bench,"cases":meta},open(f"{O}/PLUGIN_BASELINE.json","w"),indent=1)
P=lambda x:"-" if x is None else f"{100*x:.1f}%"
L=[]
w=L.append
w("# Plugin baseline: current .cml models on CPU (ml-core engine)\n")
w("Measured 2026-10-09 with the plugin's own inference engine (`NnCompletion.complete`, default `Options`: SPM, healing WORD_EOL, ctx 2000, maxPrefix 1024, suffixTokens 512, maxNew 48, repetition guard) from `github.com/dvislobokov/idea-ml-completion` @ `ae6c50c` (clone, not modified upstream). Harness: `tools/plugin-baseline/` (PluginBaseline.kt test-scope main + python). Raw numbers: `PLUGIN_BASELINE.json`. Models: `cs-nn-31m-e2-lr2e3`, `cs-nn-50m-e3-lr2e3`, `go-nn-31m-e2`, `go-nn-50m-e3-lr2e3`.\n")
w("## Setup and caveats\n")
w("- Data: our HF datasets (`dvislobokov/csharp-ml-complation`, `go-ml-complation`), **test split**, 2000 (C#) / 2001 (Go) positions per language, seed 20261009, stratified by `caret_kind` (floor 60 per kind + remainder proportional to the kind's size; so rare kinds are over-represented vs the natural distribution). Empty/whitespace-only targets excluded. `before`/`after` rebuilt from the `corpus` full file: `before = content[:caret_byte]`, `after = content[caret_byte+len(target):]` (the target is erased, the rest of the line after it is empty or trailing whitespace); `content[caret:caret+len(target)] == target_text` verified for every position.")
w(f"- Mismatches: C# 949/2000 sampled positions are in files with a UTF-8 BOM: `caret_byte_offset` counts the BOM (3 bytes) but corpus `content` has none, so the offset is shifted by -3 there (verified by target match; 0 unresolvable mismatches, 0 sha mismatches, 0 missing files). Go: 0 mismatches.")
w("- **Optimistic caveat**: test repositories (>=35 stars C#, >=100 Go) were very likely in the engine's training corpus (>=20 stars), so the accuracy is an upper-ish bound for these models. `eval-fresh/` (5..19 stars) was NOT available on the HF datasets (404) when the main run finished, so no clean-set numbers.")
w("- Metric: `exact` = generated text (typed remainder stripped, closers trimmed by the engine) == `target_text`, both trailing-whitespace-trimmed. Position-level (one row per caret), not the engine's own `eval_inline` sampling, and our carets are a different mix (many hard `line_start`/`log_message`/`lambda_body`/`after_operator` carets), so absolute exact rates are well below the 50 % (C#) / 64 % (Go) in the engine docs. Do not compare to those directly.")
w("- Engine quirk found: when the caret is right after a typed space/tab that is not line indentation (`throw |`, `as |`, `in |`, `new |`: 409/2000 C# and similar in Go), the engine does not heal the space and the model emits ` exception...` with a leading space (double space on insertion). Strict `exact` counts this as wrong; `exact_lenient` additionally accepts the text with its leading spaces stripped when `before` ends with a space/tab. Both are reported; whether the plugin strips the duplicate space is not visible from the engine repo, so treat lenient as the plugin's likely upper bound.")
w("- Shown/precision: `shown` = confProd >= gate and not punct-only (`);`, `}`) and not repetition-stopped and text non-empty (engine `show` semantics, computed offline from per-position confProd). `plugin policy` = gate 0.5 when `before` ends with `.`/`->`/`::`, else 0.7. Shown rate is over all sampled positions; precision = exact among shown.\n")
w("## Accuracy (strict exact / lenient exact)\n")
w("| model | n | exact@all strict | exact@all lenient | gate | shown | precision strict | precision lenient |")
w("|---|---|---|---|---|---|---|---|")
for m in models:
    a=acc[m]; s=a["exact"]; l=a["exact_lenient"]
    for g,name in (("gate_0.5","0.5"),("gate_0.7","0.7"),("gate_0.8","0.8"),("plugin_policy(0.7, 0.5 after dot)","plugin policy")):
        w(f"| {m} | {a['n']} | {P(s['exact_match_all_positions_unconditional'])} | {P(l['exact_match_all_positions_unconditional'])} | {name} | {P(s[g]['shown_rate'])} | {P(s[g]['precision'])} | {P(l[g]['precision'])} |")
w("\n`exact@all` is the unconditional rest-of-line exact-match rate over all sampled positions (what a model must beat when it always suggests).\n")
w("## Break-down by caret_kind (lenient exact; strict in JSON)\n")
for lang,ms in (("C#",[m for m in models if m.startswith("cs")]),("Go",[m for m in models if m.startswith("go")])):
    kinds=list(acc[ms[0]]["exact_lenient"]["by_kind"])
    w(f"### {lang}\n")
    hdr="| caret_kind | n | "+" | ".join(f"{m} exact@all / shown@0.7 / prec@0.7" for m in ms)+" |"
    w(hdr); w("|---|---|"+"---|"*len(ms))
    for k in kinds:
        row=f"| {k} | {acc[ms[0]]['exact_lenient']['by_kind'][k]['n']} |"
        for m in ms:
            o=acc[m]["exact_lenient"]["by_kind"][k]; row+=f" {P(o['exact_all'])} / {P(o['gate_0.7']['shown_rate'])} / {P(o['gate_0.7']['precision'])} |"
        w(row)
    w("")
w("Small per-kind n (60-300) means +-3-8 pp noise; per-kind precision on few shown items is very noisy.\n")
w("## Latency (this CPU, native q8 kernels, 8 threads)\n")
w("Server: 128-vCPU AMD EPYC 9754, shared with other jobs (load average 7-15 during the runs), JVM pinned with `taskset -c 0-7`, `NnModel(nThreads=8)`, kernels `native-avx512-vnni-q8`, JDK 21. **This is a big server, not a laptop**: AVX-512 VNNI, no thermal limits; laptop numbers will differ (the engine docs quote ~150 ms for a 1500-token cold prefill at 8 threads on their reference machine; we measured ~235-250 ms for the 31M model on this shared host, cause not investigated).\\n")
w("Method: 25 discarded warm-up completions (JIT), then 40 positions per model from our test cases with real prompts of 1300-1700 tokens (mean ~1540). **Cold** = fresh session (no KV reuse), full `complete()` incl. healing, tokenization, prefill and greedy decode of the line. **Prefill only** = the same prompt through `session.prefill` cold. **Warm keystroke** = the position's cold completion done first, then the user 'types' 1 (resp. 2) further characters of the target: `complete()` again on the same session (prefix reuse).\n")
w("| model | prompt tok (mean) | gen tok (mean) | cold complete p50 / p95 ms | prefill only p50 ms | decode ms/token (est.) | warm keystroke p50 / p95 ms (mean) |")
w("|---|---|---|---|---|---|---|")
for m in models:
    x=lat[m]; w(f"| {m} | {x['prompt_tokens']['mean']:.0f} | {x['gen_tokens']['mean']:.1f} | {x['cold_complete_ms']['p50']:.0f} / {x['cold_complete_ms']['p95']:.0f} | {x['cold_prefill_only_ms']['p50']:.0f} | {x['cold_decode_per_token_ms_est']:.2f} | {x['warm_keystroke1_ms']['p50']:.1f} / {x['warm_keystroke1_ms']['p95']:.0f} ({x['warm_keystroke1_ms']['mean']:.1f}) |")
w("\nWarm p95 is dominated by keystrokes where the healed boundary or the 256-token stable-prefix cut moves and more of the prompt is recomputed; the median is the typical case. (Warm keystroke #2 is the same, see JSON.)\n")
w("Existing `NnBench` (random 1500-token prompt, 20 gen, `--reuse 8` new prompt tokens, real weights, 20 measured runs, 8 threads):\n")
w("| model | params | prefill ms | decode ms/token | line ms (cold) | line ms (8 new tokens reused) | RSS MB | file MB |"); w("|---|---|---|---|---|---|---|---|")
for m in models:
    b=bench[m]; w(f"| {m} | {int(b['params'])/1e6:.1f}M | {b['prefill_ms']} | {b['decode_ms_tok']} | {b['line_ms']} | {b['line_reuse8_ms']} | {float(b['rss_mb']):.0f} | {b['file_mb']} |")
w("\nFirst call (cold JVM/JIT) is 1.6-1.8x slower than steady state (`first_line_ms` in JSON).\n")
w("## What a new model has to beat\n")
w("- Accuracy: on this test mix, 31M/50M give 25-31 % (C#) and 29-30 % (Go) unconditional strict rest-of-line exact (lenient 29-32 % / 32-35 %); at the plugin gate (0.7, 0.5 after a dot) they show 13 % (C#) / 17.5-18 % (Go) of positions at ~82-85 % (strict) / ~87-90 % (lenient) precision. Optimistic (train/test overlap).")
w("- Latency: cold ~1500-token prompt 260 ms (31M) / 410 ms (50M) at 8 native threads; warm keystroke median 11-18 ms (p95 40-97 ms).")
w("- Memory: ~335-385 MB RSS for 8-thread JVM + mmap'd model.\n")
w("## Reproduce\n")
w("`tools/plugin-baseline/`: `dl.py` (HF test shards) -> `mkcases.py <cs|go> <dir> cases.tsv 2000` -> `PluginBaseline.kt` copied to `ml-core/src/test/kotlin/io/github/completionml/core/nn/` in a clone, `./gradlew :ml-core:testClasses`, classpath via `:ml-core:printBenchClasspath`; `PluginBaseline acc|lat ...`; `analyze.py`; `make_report.py`. Server work dir: `/srv/mlbench` (raw per-position TSVs in `out/`).")
open(f"{O}/PLUGIN_BASELINE.md","w").write("\n".join(L)+"\n")
