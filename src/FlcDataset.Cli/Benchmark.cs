using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FlcDataset.Core;

namespace FlcDataset.Cli;

public sealed record BenchmarkArgs
{
    public required string Repo { get; init; }
    public required string Config { get; init; }
    public required string Out { get; init; }
    public bool Overwrite { get; init; }
    public int Workers { get; init; }
    public bool Trusted { get; init; }
    public bool SkipSemantic { get; init; }
    public string? Solution { get; init; }
}

/// <summary>
/// Runs each experiment as an isolated child process of this CLI (fresh JIT/GC/heap, so peak memory and cold start are
/// attributable to one run), then aggregates run manifests into benchmark.json and benchmark.md.
/// </summary>
public static class Benchmark
{
    sealed record PlannedRun(string Name, string Experiment, string Description, string[] Args, string? OutDir);

    sealed record RunOutcome
    {
        public required string Name { get; init; }
        public required string Experiment { get; init; }
        public required string Description { get; init; }
        public required string Command { get; init; }
        public required int ExitCode { get; init; }
        public required double ProcessWallMs { get; init; }
        public string? OutDir { get; init; }
        public JsonElement? Manifest { get; init; }
        public JsonElement? Summary { get; init; }
        public JsonElement? Validation { get; init; }
        public double? ValidationMs { get; init; }
        public string? StderrTail { get; init; }
    }

    public static async Task<int> Run(BenchmarkArgs a, CancellationToken ct)
    {
        var outRoot = Path.GetFullPath(a.Out);
        if (Directory.Exists(outRoot) && Directory.EnumerateFileSystemEntries(outRoot).Any())
        {
            if (!a.Overwrite) { Console.Error.WriteLine($"{outRoot} exists; pass --overwrite"); return 2; }
            Directory.Delete(outRoot, true);
        }
        Directory.CreateDirectory(outRoot);
        var runsDir = Path.Combine(outRoot, "runs");
        string R(string n) => Path.Combine(runsDir, n);
        var cfg = DatasetConfig.Load(a.Config);
        string[] Common(string outDir) => ["--repo", a.Repo, "--config", a.Config, "--out", outDir];
        var w = a.Workers.ToString(CultureInfo.InvariantCulture);

        var plan = new List<PlannedRun>
        {
            new("E0-discover", "E0", "Discovery + filtering + corpus manifest", ["discover", .. Common(R("E0-discover"))], R("E0-discover")),
            new("E1-syntax-seq-1", "E1", "Syntax-only, sequential baseline (1st process)", ["extract", .. Common(R("E1-syntax-seq-1")), "--workers", "1"], R("E1-syntax-seq-1")),
            new("E1-syntax-seq-2", "E1", "Syntax-only, sequential (2nd process, warm OS cache)", ["extract", .. Common(R("E1-syntax-seq-2")), "--workers", "1"], R("E1-syntax-seq-2")),
            new($"E1-syntax-par{w}", "E1", $"Syntax-only, {w} bounded workers", ["extract", .. Common(R($"E1-syntax-par{w}")), "--workers", w], R($"E1-syntax-par{w}")),
            new($"E1-syntax-par{w}-gzip", "E1", $"Syntax-only, {w} workers, gzip shards", ["extract", .. Common(R($"E1-syntax-par{w}-gzip")), "--workers", w, "--gzip"], R($"E1-syntax-par{w}-gzip")),
        };
        if (!a.SkipSemantic)
        {
            var frac = cfg.Semantic.SubsetFraction.ToString(CultureInfo.InvariantCulture);
            string[] Sol() => a.Solution is null ? [] : ["--solution", a.Solution];
            plan.Add(new PlannedRun("E2-adhoc-par", "E2", $"Semantic subset ({frac}) — safe adhoc source (no MSBuild), {w} workers",
                ["extract", .. Common(R("E2-adhoc-par")), "--workers", w, "--mode", "semantic_best_effort", "--semantic-source", "adhoc"], R("E2-adhoc-par")));
            if (a.Trusted)
            {
                plan.Add(new PlannedRun("E2-msbuild-seq", "E2", $"Semantic subset ({frac}) — trusted MSBuild workspace, sequential",
                    ["extract", .. Common(R("E2-msbuild-seq")), "--workers", "1", "--mode", "semantic_best_effort", "--trusted-project-evaluation", .. Sol()], R("E2-msbuild-seq")));
                plan.Add(new PlannedRun("E2-msbuild-par", "E2", $"Semantic subset ({frac}) — trusted MSBuild workspace, {w} workers",
                    ["extract", .. Common(R("E2-msbuild-par")), "--workers", w, "--mode", "semantic_best_effort", "--trusted-project-evaluation", .. Sol()], R("E2-msbuild-par")));
                plan.Add(new PlannedRun("E3-msbuild-full", "E3", $"Semantic on all eligible samples — trusted MSBuild, {w} workers",
                    ["extract", .. Common(R("E3-msbuild-full")), "--workers", w, "--mode", "semantic_best_effort", "--trusted-project-evaluation", "--semantic-fraction", "1.0", .. Sol()], R("E3-msbuild-full")));
            }
        }

        var outcomes = new List<RunOutcome>();
        foreach (var run in plan)
        {
            ct.ThrowIfCancellationRequested();
            Console.Error.WriteLine($"[benchmark] {run.Name}: {run.Description}");
            var (code, ms, stderr) = await Child(run.Args, ct);
            JsonElement? Load(string f) => run.OutDir is not null && File.Exists(Path.Combine(run.OutDir, f))
                ? JsonDocument.Parse(File.ReadAllText(Path.Combine(run.OutDir, f))).RootElement.Clone() : null;
            JsonElement? validation = null;
            double? vms = null;
            if (code == 0 && run.OutDir is not null)
            {
                var (vcode, vwall, _) = await Child(["validate", "--dataset", run.OutDir, "--repo", a.Repo], ct);
                vms = vwall;
                validation = Load("validation.json");
                if (vcode != 0) Console.Error.WriteLine($"[benchmark] validation FAILED for {run.Name}");
            }
            outcomes.Add(new RunOutcome
            {
                Name = run.Name, Experiment = run.Experiment, Description = run.Description, Command = "flc-dataset " + string.Join(' ', run.Args),
                ExitCode = code, ProcessWallMs = Math.Round(ms, 1), OutDir = run.OutDir, Manifest = Load("run-manifest.json"), Summary = Load("summary.json"),
                Validation = validation, ValidationMs = vms is null ? null : Math.Round(vms.Value, 1),
                StderrTail = code == 0 ? null : stderr,
            });
        }

        // E5: determinism — identical outputs across processes and worker counts.
        var e5 = new Dictionary<string, object>();
        var seq1 = R("E1-syntax-seq-1");
        foreach (var other in new[] { R("E1-syntax-seq-2"), R($"E1-syntax-par{w}") })
            if (Directory.Exists(seq1) && Directory.Exists(other))
                e5[$"{Path.GetFileName(seq1)} vs {Path.GetFileName(other)}"] = Commands.CompareDirs(seq1, other);
        if (Directory.Exists(R("E2-msbuild-seq")) && Directory.Exists(R("E2-msbuild-par")))
            e5["E2-msbuild-seq vs E2-msbuild-par"] = Commands.CompareDirs(R("E2-msbuild-seq"), R("E2-msbuild-par"));

        // E4: editor_snapshot vs strict_prefix on identical samples.
        var e4Source = new[] { R("E3-msbuild-full"), R("E2-msbuild-par"), R("E2-adhoc-par") }.FirstOrDefault(d => Jsonl.Find(d, "semantic.jsonl") is not null);
        var e4 = e4Source is null ? null : SemanticComparison.Analyze(e4Source);
        var adhocVsMsbuild = Directory.Exists(R("E2-adhoc-par")) && Directory.Exists(R("E2-msbuild-par"))
            ? SemanticComparison.CompareSources(R("E2-adhoc-par"), R("E2-msbuild-par")) : null;

        var report = new Dictionary<string, object?>
        {
            ["schema_version"] = "benchmark/v1",
            ["generated_utc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["repo"] = Path.GetFullPath(a.Repo),
            ["config"] = Path.GetFullPath(a.Config),
            ["parallel_workers"] = a.Workers,
            ["trusted_project_evaluation"] = a.Trusted,
            ["environment"] = FlcDataset.Extraction.ExtractionPipeline.EnvironmentInfo(),
            ["dotnet_sdk"] = Sh("dotnet", "--version", a.Repo),
            ["runs"] = outcomes,
            ["e4_visibility_comparison"] = e4,
            ["e4_source"] = e4Source is null ? null : Path.GetFileName(e4Source),
            ["semantic_source_comparison"] = adhocVsMsbuild,
            ["e5_determinism"] = e5,
            ["skipped"] = a.SkipSemantic ? new[] { "E2", "E3", "E4 (requested --skip-semantic)" } :
                a.Trusted ? Array.Empty<string>() : ["E2-msbuild", "E3-msbuild (no --trusted-project-evaluation)"],
        };
        var jsonPath = Path.Combine(outRoot, "benchmark.json");
        await File.WriteAllTextAsync(jsonPath, FlcJson.Serialize(report, indented: true) + "\n", ct);
        var md = BenchmarkReport.Render(report, outcomes.Select(o => (o.Name, o.Experiment, o.Description, o.ExitCode, o.ProcessWallMs, o.Manifest, o.Summary, o.Validation, o.ValidationMs)).ToList(), e4, adhocVsMsbuild, e5);
        await File.WriteAllTextAsync(Path.Combine(outRoot, "benchmark.md"), md, ct);
        Console.WriteLine(md);
        return outcomes.All(o => o.ExitCode == 0) ? 0 : 1;
    }

    static string? Sh(string file, string arg, string cwd)
    {
        try
        {
            var psi = new ProcessStartInfo(file, arg) { RedirectStandardOutput = true, UseShellExecute = false, WorkingDirectory = cwd };
            using var p = Process.Start(psi)!;
            var s = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return s;
        }
        catch (Exception e) { return "unavailable: " + e.Message; }
    }

    static async Task<(int Code, double Ms, string Stderr)> Child(string[] args, CancellationToken ct)
    {
        var self = Environment.ProcessPath!;
        var psi = new ProcessStartInfo(self) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        if (Path.GetFileNameWithoutExtension(self) == "dotnet") psi.ArgumentList.Add(typeof(Benchmark).Assembly.Location);
        foreach (var x in args) psi.ArgumentList.Add(x);
        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi)!;
        var stderrTail = new StringBuilder();
        var errTask = Task.Run(async () =>
        {
            while (await p.StandardError.ReadLineAsync(ct) is { } line)
            {
                if (line.Contains("[error]") || line.Contains("[warn]") || line.Contains("Exception")) Console.Error.WriteLine("    " + line[..Math.Min(line.Length, 300)]);
                stderrTail.AppendLine(line);
                if (stderrTail.Length > 8000) stderrTail.Remove(0, stderrTail.Length - 8000);
            }
        }, ct);
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        await errTask;
        await outTask;
        return (p.ExitCode, sw.Elapsed.TotalMilliseconds, stderrTail.ToString());
    }
}
