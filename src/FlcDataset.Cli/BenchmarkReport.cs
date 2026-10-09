using System.Globalization;
using System.Text;
using System.Text.Json;
using FlcDataset.Core;

namespace FlcDataset.Cli;

public static class BenchmarkReport
{
    static string N(double v, int d = 1) => v.ToString("N" + d, CultureInfo.InvariantCulture);
    static string Mib(double bytes) => N(bytes / 1048576.0, 1);

    static double D(JsonElement? e, params string[] path)
    {
        if (e is null) return double.NaN;
        var cur = e.Value;
        foreach (var p in path)
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(p, out cur)) return double.NaN;
        }
        return cur.ValueKind == JsonValueKind.Number ? cur.GetDouble() : double.NaN;
    }

    static string Cell(double v, int d = 1) => double.IsNaN(v) ? "–" : N(v, d);

    public static string Render(Dictionary<string, object?> report,
        List<(string Name, string Exp, string Desc, int Exit, double WallMs, JsonElement? Manifest, JsonElement? Summary, JsonElement? Validation, double? ValMs)> runs,
        Dictionary<string, object?>? e4, Dictionary<string, object?>? sources, Dictionary<string, object> e5)
    {
        var sb = new StringBuilder();
        var env = (Dictionary<string, object?>)report["environment"]!;
        sb.AppendLine("# FLC dataset benchmark");
        sb.AppendLine();
        sb.AppendLine($"- Generated: {report["generated_utc"]}");
        sb.AppendLine($"- Repository: `{report["repo"]}`");
        var rev = runs.Select(r => r.Manifest).FirstOrDefault(m => m is not null)?.GetProperty("repository").GetProperty("revision").GetString();
        sb.AppendLine($"- Revision: `{rev ?? "unresolved"}`");
        sb.AppendLine($"- Machine: {env["cpu_model"]} · {env["processors"]} logical CPUs · {Mib(Convert.ToDouble(env["total_memory_bytes"]))} MiB RAM · {env["os"]}");
        sb.AppendLine($"- Runtime: {env["dotnet_runtime"]} · Roslyn {env["roslyn_informational_version"]?.ToString()?.Split('+')[0]} · SDK {report["dotnet_sdk"]}");
        sb.AppendLine($"- Each run is a separate child process (cold JIT, fresh heap). Peak memory = sampled every 25 ms.");
        sb.AppendLine();

        sb.AppendLine("## Runs");
        sb.AppendLine();
        sb.AppendLine("| Run | Exit | Workers | Process wall s | Pipeline wall s | CPU s | Peak RSS MiB | Peak managed MiB | Alloc MiB | Files | Samples | Samples/s | Source MiB/s | Output MiB | Validation |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in runs)
        {
            var m = r.Manifest;
            var ok = r.Validation is { } v ? (v.GetProperty("report").GetProperty("ok").GetBoolean() ? "ok" : "**FAIL**") : "–";
            sb.AppendLine($"| {r.Name} | {r.Exit} | {Cell(D(m, "workers"), 0)} | {N(r.WallMs / 1000, 2)} | {Cell(D(m, "stage_timings_ms", "pipeline_wall") / 1000, 2)} | " +
                          $"{Cell(D(m, "resources", "cpu_ms") / 1000, 2)} | {Cell(D(m, "resources", "peak_working_set_bytes") / 1048576)} | {Cell(D(m, "resources", "peak_managed_bytes") / 1048576)} | " +
                          $"{Cell(D(m, "resources", "total_allocated_bytes") / 1048576)} | {Cell(D(m, "counters", "files.accepted"), 0)} | {Cell(D(m, "counters", "samples.written"), 0)} | " +
                          $"{Cell(D(m, "throughput", "samples_per_sec"))} | {Cell(D(m, "throughput", "source_mib_per_sec"), 3)} | {Cell(D(m, "throughput", "output_bytes") / 1048576, 2)} | {ok} |");
        }
        sb.AppendLine();
        sb.AppendLine("`Pipeline wall` covers discovery → extraction → (semantic) → serialization; workspace load / restore are reported separately below. Validation runs as its own process after each run.");
        sb.AppendLine();

        sb.AppendLine("## Stage timings (ms)");
        sb.AppendLine();
        sb.AppendLine("Wall-clock stages on the main thread, and summed worker time (can exceed wall time when parallel).");
        sb.AppendLine();
        sb.AppendLine("| Run | workspace_load | discovery_io | pipeline_wall | writer_serialization | finalize | Σ parse | Σ extract | Σ semantic | validation |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in runs)
        {
            var m = r.Manifest;
            sb.AppendLine($"| {r.Name} | {Cell(D(m, "stage_timings_ms", "workspace_load"), 0)} | {Cell(D(m, "stage_timings_ms", "discovery_io"), 0)} | {Cell(D(m, "stage_timings_ms", "pipeline_wall"), 0)} | " +
                          $"{Cell(D(m, "stage_timings_ms", "writer_serialization"), 0)} | {Cell(D(m, "stage_timings_ms", "finalize_outputs"), 0)} | {Cell(D(m, "stage_worker_time_ms", "syntax_parse"), 0)} | " +
                          $"{Cell(D(m, "stage_worker_time_ms", "syntax_extract"), 0)} | {Cell(D(m, "stage_worker_time_ms", "semantic"), 0)} | {(r.ValMs is null ? "–" : N(r.ValMs.Value, 0))} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Latency (ms)");
        sb.AppendLine();
        sb.AppendLine("| Run | file syntax p50 | p95 | p99 | max | semantic/sample p50 | p95 | p99 | max | semantic analyses |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in runs.Where(r => r.Exp != "E0"))
        {
            var m = r.Manifest;
            sb.AppendLine($"| {r.Name} | {Cell(D(m, "latency", "file_syntax", "p50_ms"), 2)} | {Cell(D(m, "latency", "file_syntax", "p95_ms"), 2)} | {Cell(D(m, "latency", "file_syntax", "p99_ms"), 2)} | {Cell(D(m, "latency", "file_syntax", "max_ms"), 1)} | " +
                          $"{Cell(D(m, "latency", "sample_semantic", "p50_ms"), 2)} | {Cell(D(m, "latency", "sample_semantic", "p95_ms"), 2)} | {Cell(D(m, "latency", "sample_semantic", "p99_ms"), 2)} | {Cell(D(m, "latency", "sample_semantic", "max_ms"), 1)} | {Cell(D(m, "latency", "sample_semantic", "count"), 0)} |");
        }
        sb.AppendLine();

        var e0 = runs.FirstOrDefault(r => r.Exp == "E0");
        if (e0.Summary is { } s0)
        {
            sb.AppendLine("## E0 — discovery");
            sb.AppendLine();
            sb.AppendLine("| Counter | Value |");
            sb.AppendLine("|---|---|");
            foreach (var p in s0.GetProperty("files").EnumerateObject()) sb.AppendLine($"| files.{p.Name} | {p.Value} |");
            sb.AppendLine($"| accepted source bytes | {s0.GetProperty("accepted_source_bytes")} |");
            sb.AppendLine($"| accepted source lines | {s0.GetProperty("accepted_source_lines")} |");
            sb.AppendLine($"| accepted source chars (UTF-16) | {s0.GetProperty("accepted_source_chars")} |");
            sb.AppendLine();
        }

        var e1 = runs.FirstOrDefault(r => r.Name == "E1-syntax-seq-1");
        if (e1.Summary is { } s1)
        {
            sb.AppendLine("## E1 — syntax-only samples");
            sb.AppendLine();
            sb.AppendLine("| Caret kind | Samples |");
            sb.AppendLine("|---|---|");
            foreach (var p in s1.GetProperty("samples").EnumerateObject().Where(p => p.Name.StartsWith("kind."))) sb.AppendLine($"| {p.Name[5..]} | {p.Value} |");
            sb.AppendLine();
            sb.AppendLine("| Samples counter | Value |");
            sb.AppendLine("|---|---|");
            foreach (var p in s1.GetProperty("samples").EnumerateObject().Where(p => !p.Name.StartsWith("kind."))) sb.AppendLine($"| {p.Name} | {p.Value} |");
            sb.AppendLine($"| duplicate_fraction | {s1.GetProperty("duplicate_fraction")} |");
            sb.AppendLine($"| mean_target_chars | {s1.GetProperty("mean_target_chars")} |");
            sb.AppendLine();
            sb.AppendLine("Exclusions (tagged negative strata and quality rules; never silently dropped):");
            sb.AppendLine();
            sb.AppendLine("| Reason | Count |");
            sb.AppendLine("|---|---|");
            foreach (var p in s1.GetProperty("excluded").EnumerateObject()) sb.AppendLine($"| {p.Name} | {p.Value} |");
            foreach (var p in s1.GetProperty("lines").EnumerateObject()) sb.AppendLine($"| lines.{p.Name} | {p.Value} |");
            sb.AppendLine();
        }

        sb.AppendLine("## Semantic runs (E2/E3)");
        sb.AppendLine();
        sb.AppendLine("| Run | attempted | resolved | partial | fallback | failed | project compilations (miss/hit) | compile warmup ms | workspace projects | workspace diagnostics |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in runs.Where(r => r.Exp is "E2" or "E3"))
        {
            var m = r.Manifest;
            sb.AppendLine($"| {r.Name} | {Cell(D(m, "counters", "semantic.attempted"), 0)} | {Cell(D(m, "counters", "semantic.status.resolved"), 0)} | {Cell(D(m, "counters", "semantic.status.partially_resolved"), 0)} | " +
                          $"{Cell(D(m, "counters", "semantic.status.syntax_fallback"), 0)} | {Cell(D(m, "counters", "semantic.status.failed"), 0)} | " +
                          $"{Cell(D(m, "counters", "semantic.cache.project_compilation_miss"), 0)}/{Cell(D(m, "counters", "semantic.cache.project_compilation_hit"), 0)} | {Cell(D(m, "counters", "semantic.compile_warmup_ms"), 0)} | " +
                          $"{Cell(D(m, "environment", "projects_loaded"), 0)} | {Cell(D(m, "environment", "workspace_diagnostics"), 0)} |");
        }
        sb.AppendLine();

        if (e4 is not null)
        {
            sb.AppendLine($"## E4 — editor_snapshot vs strict_prefix ({e4["dataset"]}, {e4["paired_samples"]} paired samples)");
            sb.AppendLine();
            sb.AppendLine("| Metric | editor_snapshot | strict_prefix |");
            sb.AppendLine("|---|---|---|");
            var pp = (Dictionary<string, SemanticComparison.PolicyStats>)e4["per_policy"]!;
            pp.TryGetValue(VisibilityPolicy.EditorSnapshot, out var ed);
            pp.TryGetValue(VisibilityPolicy.StrictPrefix, out var st);
            void Row(string name, Func<SemanticComparison.PolicyStats, object> f) => sb.AppendLine($"| {name} | {(ed is null ? "–" : f(ed))} | {(st is null ? "–" : f(st))} |");
            Row("records", x => x.Records);
            Row("resolved", x => x.Status.GetValueOrDefault(SemanticStatus.Resolved));
            Row("partially_resolved", x => x.Status.GetValueOrDefault(SemanticStatus.PartiallyResolved));
            Row("failed", x => x.Status.GetValueOrDefault(SemanticStatus.Failed));
            Row("mean locals", x => x.MeanLocals);
            Row("mean parameters", x => x.MeanParameters);
            Row("mean this-members", x => x.MeanThisMembers);
            Row("mean receiver members", x => x.MeanReceiverMembers);
            Row("has receiver type", x => x.HasReceiverRate);
            Row("has expected type", x => x.HasExpectedTypeRate);
            Row("mean prompt chars", x => x.MeanPromptChars);
            Row("target identifier coverage", x => x.TargetIdentifierCoverage);
            Row("leak violations", x => x.LeakViolations);
            sb.AppendLine();
            sb.AppendLine($"- identical prompt: {e4["identical_prompt_fraction"]}; status differs: {e4["status_differs_fraction"]}");
            sb.AppendLine($"- editor covers a target identifier strict does not: {e4["editor_only_target_coverage_fraction"]}; reverse: {e4["strict_only_target_coverage_fraction"]}");
            sb.AppendLine();
            sb.AppendLine("By caret kind (editor_snapshot / strict_prefix):");
            sb.AppendLine();
            sb.AppendLine("```json");
            sb.AppendLine(FlcJson.Serialize(e4["by_caret_kind"], indented: true));
            sb.AppendLine("```");
            sb.AppendLine();
        }

        if (sources is not null)
        {
            sb.AppendLine("## Semantic source: safe adhoc vs trusted MSBuild (E2 subset, editor_snapshot)");
            sb.AppendLine();
            sb.AppendLine("```json");
            sb.AppendLine(FlcJson.Serialize(sources.Where(kv => kv.Key is not ("adhoc" or "msbuild")).ToDictionary(), indented: true));
            sb.AppendLine("```");
            var a = (SemanticComparison.PolicyStats)sources["adhoc"]!;
            var m = (SemanticComparison.PolicyStats)sources["msbuild"]!;
            sb.AppendLine();
            sb.AppendLine("| Metric | adhoc | msbuild |");
            sb.AppendLine("|---|---|---|");
            sb.AppendLine($"| resolved | {a.Status.GetValueOrDefault("resolved")} | {m.Status.GetValueOrDefault("resolved")} |");
            sb.AppendLine($"| partially_resolved | {a.Status.GetValueOrDefault("partially_resolved")} | {m.Status.GetValueOrDefault("partially_resolved")} |");
            sb.AppendLine($"| has receiver type | {a.HasReceiverRate} | {m.HasReceiverRate} |");
            sb.AppendLine($"| has expected type | {a.HasExpectedTypeRate} | {m.HasExpectedTypeRate} |");
            sb.AppendLine($"| target identifier coverage | {a.TargetIdentifierCoverage} | {m.TargetIdentifierCoverage} |");
            sb.AppendLine();
        }

        sb.AppendLine("## E5 — determinism");
        sb.AppendLine();
        sb.AppendLine("| Comparison | File | Identical |");
        sb.AppendLine("|---|---|---|");
        foreach (var (name, filesObj) in e5)
            foreach (var (file, v) in (Dictionary<string, object>)filesObj)
                sb.AppendLine($"| {name} | {file} | {((bool)v.GetType().GetProperty("identical")!.GetValue(v)! ? "yes" : "**NO**")} |");
        sb.AppendLine();
        if (report["skipped"] is string[] { Length: > 0 } skipped) sb.AppendLine("Skipped: " + string.Join("; ", skipped));
        return sb.ToString();
    }
}
