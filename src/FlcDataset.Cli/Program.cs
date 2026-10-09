using System.CommandLine;
using System.Diagnostics;
using FlcDataset.Cli;
using FlcDataset.Core;
using FlcDataset.Extraction;
using FlcDataset.Semantics;

var root = new RootCommand("flc-dataset — C# full-line completion dataset builder (Roslyn). Input code is untrusted; see docs/SECURITY.md.");

// ---------------------------------------------------------------- pilot fetch
var urlOpt = new Option<string>("--url") { Description = "Git URL (https only; host must be allowlisted)", DefaultValueFactory = _ => "https://github.com/dotnet/eShop.git" };
var refOpt = new Option<string>("--ref") { Description = "Branch, tag or commit to check out", DefaultValueFactory = _ => "main" };
var destOpt = new Option<string>("--dest") { Description = "Checkout directory", Required = true };
var pinOutOpt = new Option<string>("--pin-out") { Description = "Where to write the pin record", DefaultValueFactory = _ => "artifacts/pilot-pin.json" };
var fetch = new Command("fetch", "Clone (or reuse) the pilot repository and record the resolved commit SHA") { urlOpt, refOpt, destOpt, pinOutOpt };
fetch.SetAction(pr => Commands.Fetch(pr.GetValue(urlOpt)!, pr.GetValue(refOpt)!, pr.GetValue(destOpt)!, pr.GetValue(pinOutOpt)!));
root.Subcommands.Add(new Command("pilot", "Pilot repository management") { fetch });

// ---------------------------------------------------------------- shared options
Option<string> RepoOpt() => new("--repo") { Description = "Repository checkout (read-only input)", Required = true };
Option<string> ConfigOpt() => new("--config") { Description = "Dataset config JSON", DefaultValueFactory = _ => "configs/eshop.pilot.json" };
Option<string> OutOpt() => new("--out") { Description = "Output directory", Required = true };
Option<bool> OverwriteOpt() => new("--overwrite") { Description = "Replace an existing output directory" };
Option<bool> GzipOpt() => new("--gzip") { Description = "Write .jsonl.gz shards" };
Option<int> WorkersOpt() => new("--workers") { Description = "Bounded worker count (1 = sequential baseline)", DefaultValueFactory = _ => 1 };

// ---------------------------------------------------------------- discover
{
    var repo = RepoOpt(); var cfg = ConfigOpt(); var outo = OutOpt(); var ow = OverwriteOpt(); var gz = GzipOpt();
    var cmd = new Command("discover", "E0: file discovery, filtering and corpus manifest (no caret extraction)") { repo, cfg, outo, ow, gz };
    cmd.SetAction((pr, ct) => Commands.Extract(new ExtractArgs
    {
        Repo = pr.GetValue(repo)!, Config = pr.GetValue(cfg)!, Out = pr.GetValue(outo)!, Overwrite = pr.GetValue(ow), Gzip = pr.GetValue(gz),
        Mode = "discover", Workers = 1,
    }, ct));
    root.Subcommands.Add(cmd);
}

// ---------------------------------------------------------------- extract
{
    var repo = RepoOpt(); var cfg = ConfigOpt(); var outo = OutOpt(); var ow = OverwriteOpt(); var gz = GzipOpt(); var workers = WorkersOpt();
    var mode = new Option<string>("--mode") { Description = "syntax_only | semantic_best_effort | semantic_required", DefaultValueFactory = _ => "syntax_only" };
    mode.AcceptOnlyFromAmong("syntax_only", "semantic_best_effort", "semantic_required");
    var trusted = new Option<bool>("--trusted-project-evaluation") { Description = "Allow MSBuild project evaluation (executes SDK/NuGet targets of the input). Required for --semantic-source msbuild." };
    var source = new Option<string>("--semantic-source") { Description = "msbuild (trusted, full references) | adhoc (safe: no MSBuild, framework refs only)", DefaultValueFactory = _ => "msbuild" };
    source.AcceptOnlyFromAmong("msbuild", "adhoc");
    var solution = new Option<string?>("--solution") { Description = "Solution/filter/project relative to repo (default: config semantic.solution)" };
    var fraction = new Option<double?>("--semantic-fraction") { Description = "Override semantic.subset_fraction (1.0 = all samples)" };
    var engine = new Option<string?>("--semantic-engine") { Description = "Override semantic.engine: auto (speculative member binding, fork fallback) | fork" };
    engine.AcceptOnlyFromAmong("auto", "fork");
    var cmd = new Command("extract", "E1–E3: extract corpus + FLC samples, optionally with semantic enrichment") { repo, cfg, outo, ow, gz, workers, mode, trusted, source, solution, fraction, engine };
    cmd.SetAction((pr, ct) => Commands.Extract(new ExtractArgs
    {
        Repo = pr.GetValue(repo)!, Config = pr.GetValue(cfg)!, Out = pr.GetValue(outo)!, Overwrite = pr.GetValue(ow), Gzip = pr.GetValue(gz),
        Mode = pr.GetValue(mode)!, Workers = pr.GetValue(workers), Trusted = pr.GetValue(trusted), SemanticSource = pr.GetValue(source)!,
        Solution = pr.GetValue(solution), SemanticFraction = pr.GetValue(fraction), SemanticEngine = pr.GetValue(engine),
    }, ct));
    root.Subcommands.Add(cmd);
}

// ---------------------------------------------------------------- validate
{
    var ds = new Option<string>("--dataset") { Description = "Dataset directory", Required = true };
    var repo = new Option<string?>("--repo") { Description = "Repository checkout for exact source reconstruction" };
    var cmd = new Command("validate", "Validate schema, exact reconstruction, ids, splits, checksums and leakage audit") { ds, repo };
    cmd.SetAction(pr => Commands.Validate(pr.GetValue(ds)!, pr.GetValue(repo)));
    root.Subcommands.Add(cmd);
}

// ---------------------------------------------------------------- inspect
{
    var ds = new Option<string>("--dataset") { Description = "Dataset directory", Required = true };
    var count = new Option<int>("--count") { Description = "Samples to show", DefaultValueFactory = _ => 5 };
    var kind = new Option<string?>("--kind") { Description = "Filter by caret_kind" };
    var id = new Option<string?>("--sample-id") { Description = "Show one sample" };
    var cmd = new Command("inspect", "Pretty-print samples with caret, target, <EOL> and semantic prompt") { ds, count, kind, id };
    cmd.SetAction(pr => Commands.Inspect(pr.GetValue(ds)!, pr.GetValue(count), pr.GetValue(kind), pr.GetValue(id)));
    root.Subcommands.Add(cmd);
}

// ---------------------------------------------------------------- render
{
    var ds = new Option<string>("--dataset") { Description = "Dataset directory (samples.jsonl [+ semantic.jsonl])", Required = true };
    var outo = new Option<string>("--out") { Description = "Output directory for prompts.<split>.jsonl", Required = true };
    var maxCode = new Option<int>("--max-code-chars") { DefaultValueFactory = _ => 4000, Description = "Code window budget (chars; no tokenizer pinned yet)" };
    var maxSem = new Option<int>("--max-semantic-chars") { DefaultValueFactory = _ => 900, Description = "Semantic block budget (chars)" };
    var policy = new Option<string>("--policy") { DefaultValueFactory = _ => "editor_snapshot", Description = "Which semantic visibility policy to render" };
    policy.AcceptOnlyFromAmong("editor_snapshot", "strict_prefix");
    var preview = new Option<int>("--preview") { DefaultValueFactory = _ => 100, Description = "Examples written to preview.md for manual inspection" };
    var noTypes = new Option<bool>("--no-types") { Description = "Omit TYPE lines (flc-prompt/v1 layout)" };
    var cmd = new Command("render", "Serialize samples into flc-prompt/v2 training records (prompt + completion with <|eol|>)") { ds, outo, maxCode, maxSem, policy, preview, noTypes };
    cmd.SetAction(pr => Commands.Render(pr.GetValue(ds)!, pr.GetValue(outo)!, new PromptOptions
    {
        MaxCodeChars = pr.GetValue(maxCode), MaxSemanticChars = pr.GetValue(maxSem), Policy = pr.GetValue(policy)!,
        IncludeTypes = !pr.GetValue(noTypes), Format = pr.GetValue(noTypes) ? PromptRenderer.FormatV1 : PromptRenderer.FormatV2,
    }, pr.GetValue(preview)));
    root.Subcommands.Add(cmd);
}

// ---------------------------------------------------------------- semantic-compare
{
    var a = new Option<string>("--a") { Required = true, Description = "Dataset with semantic.jsonl (reference)" };
    var b = new Option<string>("--b") { Required = true, Description = "Dataset with semantic.jsonl (candidate)" };
    var policy = new Option<string>("--policy") { DefaultValueFactory = _ => "editor_snapshot" };
    var examples = new Option<int>("--examples") { DefaultValueFactory = _ => 5, Description = "Differing prompt pairs to print" };
    var cmd = new Command("semantic-compare", "Agreement of two semantic sidecars on common samples (engine/source regression check)") { a, b, policy, examples };
    cmd.SetAction(pr => Commands.SemanticCompare(pr.GetValue(a)!, pr.GetValue(b)!, pr.GetValue(policy)!, pr.GetValue(examples)));
    root.Subcommands.Add(cmd);
}

// ---------------------------------------------------------------- compare
{
    var a = new Option<string>("--a") { Required = true, Description = "First dataset directory" };
    var b = new Option<string>("--b") { Required = true, Description = "Second dataset directory" };
    var cmd = new Command("compare", "E5: compare deterministic outputs of two runs (byte-identical check)") { a, b };
    cmd.SetAction(pr => Commands.Compare(pr.GetValue(a)!, pr.GetValue(b)!));
    root.Subcommands.Add(cmd);
}

// ---------------------------------------------------------------- benchmark
{
    var repo = RepoOpt(); var cfg = ConfigOpt(); var outo = OutOpt(); var ow = OverwriteOpt();
    var workers = new Option<int>("--workers") { Description = "Parallel worker count for parallel runs", DefaultValueFactory = _ => Math.Min(8, Environment.ProcessorCount) };
    var trusted = new Option<bool>("--trusted-project-evaluation") { Description = "Enable MSBuild semantic experiments (E2/E3 msbuild)" };
    var skipSem = new Option<bool>("--skip-semantic") { Description = "Run only E0/E1/E5" };
    var solution = new Option<string?>("--solution") { Description = "Solution/filter for semantic runs" };
    var cmd = new Command("benchmark", "Run E0–E5 as isolated child processes; write benchmark.json and benchmark.md") { repo, cfg, outo, ow, workers, trusted, skipSem, solution };
    cmd.SetAction((pr, ct) => Benchmark.Run(new BenchmarkArgs
    {
        Repo = pr.GetValue(repo)!, Config = pr.GetValue(cfg)!, Out = pr.GetValue(outo)!, Overwrite = pr.GetValue(ow), Workers = pr.GetValue(workers),
        Trusted = pr.GetValue(trusted), SkipSemantic = pr.GetValue(skipSem), Solution = pr.GetValue(solution),
    }, ct));
    root.Subcommands.Add(cmd);
}

return await root.Parse(args).InvokeAsync();

namespace FlcDataset.Cli
{
    public sealed record ExtractArgs
    {
        public required string Repo { get; init; }
        public required string Config { get; init; }
        public required string Out { get; init; }
        public bool Overwrite { get; init; }
        public bool Gzip { get; init; }
        public required string Mode { get; init; }
        public int Workers { get; init; } = 1;
        public bool Trusted { get; init; }
        public string SemanticSource { get; init; } = "msbuild";
        public string? Solution { get; init; }
        public double? SemanticFraction { get; init; }
        public string? SemanticEngine { get; init; }
    }

    public static class Commands
    {
        static readonly string[] AllowedHosts = ["github.com"];

        public static int Fetch(string url, string gitRef, string dest, string pinOut)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !AllowedHosts.Contains(uri.Host))
            {
                Console.Error.WriteLine($"Refusing URL '{url}': only https://{string.Join("|", AllowedHosts)} is allowed.");
                return 2;
            }
            if (gitRef.StartsWith('-')) { Console.Error.WriteLine("Invalid ref"); return 2; }
            if (!Directory.Exists(Path.Combine(dest, ".git")))
            {
                if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any()) { Console.Error.WriteLine($"{dest} exists and is not a git checkout"); return 2; }
                if (!RunGit(null, "clone", "--no-tags", "--filter=blob:none", "--", url, dest)) return 1;
                if (!RunGit(dest, "checkout", "--detach", gitRef)) return 1;
            }
            else Console.Error.WriteLine($"Reusing existing checkout {dest} (not fetching; current HEAD is pinned).");
            var (sha, err) = RepositoryInfo.Git(dest, "rev-parse", "HEAD");
            if (sha is null) { Console.Error.WriteLine(err); return 1; }
            var (date, _) = RepositoryInfo.Git(dest, "log", "-1", "--format=%cI");
            var (origin, _) = RepositoryInfo.Git(dest, "config", "--get", "remote.origin.url");
            var (dirty, _) = RepositoryInfo.Git(dest, "status", "--porcelain", "--untracked-files=no");
            var pin = new { url, origin, requested_ref = gitRef, revision = sha, commit_date = date, dirty = dirty?.Length > 0, dest = Path.GetFullPath(dest), pinned_utc = DateTimeOffset.UtcNow.ToString("O") };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pinOut))!);
            File.WriteAllText(pinOut, FlcJson.Serialize(pin, indented: true) + "\n");
            Console.WriteLine(FlcJson.Serialize(pin, indented: true));
            return 0;
        }

        static bool RunGit(string? cwd, params string[] args)
        {
            var psi = new ProcessStartInfo("git") { UseShellExecute = false };
            if (cwd is not null) { psi.ArgumentList.Add("-C"); psi.ArgumentList.Add(cwd); }
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode == 0;
        }

        /// <summary>Writes into a sibling temp directory, then atomically renames; refuses to clobber unless --overwrite.</summary>
        public static async Task<int> Extract(ExtractArgs a, CancellationToken ct)
        {
            var outDir = Path.GetFullPath(a.Out);
            if (Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any() && !a.Overwrite)
            {
                Console.Error.WriteLine($"Output {outDir} exists; pass --overwrite or choose another --out.");
                return 2;
            }
            var config = DatasetConfig.Load(a.Config);
            if (a.SemanticFraction is { } f) config = config with { Semantic = config.Semantic with { SubsetFraction = f } };
            if (a.SemanticEngine is { } eng) config = config with { Semantic = config.Semantic with { Engine = eng } };
            SemanticProfile.Enabled = Environment.GetEnvironmentVariable("FLC_SEMANTIC_PROFILE") == "1";
            var tmp = outDir + ".tmp-" + Environment.ProcessId;
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            using var log = new RunLog(Path.Combine(tmp, "run.log.jsonl"));
            log.Info("cli", "start", new { a.Mode, a.Workers, a.Repo, a.Config, out_dir = outDir, a.SemanticSource, trusted = a.Trusted });

            ISemanticDocumentSource? source = null;
            var pre = new Dictionary<string, double>();
            var semEnv = new Dictionary<string, object?>();
            try
            {
                if (a.Mode is "semantic_best_effort" or "semantic_required")
                {
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        source = await CreateSource(a, config, log, ct);
                    }
                    catch (Exception e) when (e is not OperationCanceledException && a.Mode == "semantic_best_effort")
                    {
                        // Best effort: project load failure degrades every sample to a reason-coded syntax fallback.
                        log.Error("semantic", "workspace_load_failed", new { error = e.GetType().Name, e.Message });
                        semEnv["workspace_load_error"] = e.GetType().Name + ": " + e.Message;
                        source = new NullDocumentSource("project_load");
                    }
                    pre["workspace_load"] = sw.Elapsed.TotalMilliseconds;
                    foreach (var (k, v) in source.Describe()) semEnv[k] = v;
                }

                var result = await ExtractionPipeline.RunAsync(new PipelineOptions
                {
                    RepoPath = a.Repo, OutputDir = tmp, Config = config, Mode = a.Mode, Workers = a.Workers, Gzip = a.Gzip,
                    ExtractSamples = a.Mode != "discover",
                    Enricher = source is null ? null : new SemanticEnricher(source, config, a.Mode),
                    Log = log, PreStageTimingsMs = pre, SemanticEnvironment = semEnv,
                }, ct);
                log.Info("cli", "done", new { samples = result.Summary.Samples.GetValueOrDefault("written"), out_dir = outDir });
                if (SemanticProfile.Enabled)
                    File.WriteAllText(Path.Combine(tmp, "semantic-profile.json"), FlcJson.Serialize(SemanticProfile.Snapshot(), indented: true) + "\n");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.Error("cli", "failed", new { error = e.ToString() });
                Console.Error.WriteLine(e);
                return 1;
            }
            finally
            {
                source?.Dispose();
                log.Dispose();
            }
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            Directory.Move(tmp, outDir);
            Console.WriteLine(File.ReadAllText(Path.Combine(outDir, "summary.json")));
            return 0;
        }

        static async Task<ISemanticDocumentSource> CreateSource(ExtractArgs a, DatasetConfig config, RunLog log, CancellationToken ct)
        {
            if (a.SemanticSource == "msbuild")
            {
                if (!a.Trusted)
                    throw new InvalidOperationException("MSBuild semantic source requires --trusted-project-evaluation (it evaluates project files and SDK/NuGet targets).");
                var solution = a.Solution ?? config.Semantic.Solution ?? throw new InvalidOperationException("No --solution and no semantic.solution in config.");
                MsBuildDocumentSource.EnsureMsBuildRegistered(Path.GetFullPath(a.Repo));
                return await MsBuildDocumentSource.LoadAsync(a.Repo, solution, log, ct);
            }
            // Safe source: reuse discovery decisions so the same accepted files form the projects.
            var repo = RepositoryInfo.Inspect(a.Repo, config);
            var files = new Discoverer(config, repo).Discover().Where(f => f.Document is not null)
                .Select(f => (f.Record.RelativePath, f.Record.Project, f.Document!.Text)).ToList();
            var src = AdhocDocumentSource.Create(a.Repo, files);
            log.Info("semantic", "adhoc_workspace_created", src.Describe());
            return src;
        }

        public static int Validate(string dataset, string? repo)
        {
            var sw = Stopwatch.StartNew();
            var report = new DatasetValidator().Validate(dataset, repo);
            var path = Path.Combine(dataset, "validation.json");
            File.WriteAllText(path, FlcJson.Serialize(new { report, elapsed_ms = Math.Round(sw.Elapsed.TotalMilliseconds, 1) }, indented: true) + "\n");
            Console.WriteLine(FlcJson.Serialize(report, indented: true));
            return report.Ok ? 0 : 3;
        }

        public static int Inspect(string dataset, int count, string? kind, string? id)
        {
            var samplesPath = Jsonl.Find(dataset, "samples.jsonl") ?? throw new FileNotFoundException("samples.jsonl");
            var sem = Jsonl.Find(dataset, "semantic.jsonl") is { } sp
                ? Jsonl.Read<SemanticRecord>(sp).Where(r => r.VisibilityPolicy == VisibilityPolicy.EditorSnapshot).ToDictionary(r => r.SampleId)
                : new Dictionary<string, SemanticRecord>();
            var picked = Jsonl.Read<FlcSampleRecord>(samplesPath)
                .Where(s => (kind is null || s.CaretKind == kind) && (id is null || s.SampleId == id))
                .OrderBy(s => Hashing.Uniform("inspect", s.SampleId)).Take(count);
            foreach (var s in picked)
            {
                var lastLines = s.LeftContext.Split('\n')[^Math.Min(4, s.LeftContext.Split('\n').Length)..];
                Console.WriteLine($"── {s.SampleId}  {s.RelativePath}:{s.CaretLineZeroBased + 1}:{s.CaretColumnUtf16ZeroBased + 1}  kind={s.CaretKind}/{s.CaretSubkind}  split={s.Split}  semantic={s.SemanticStatus}");
                if (s.Tags.Count > 0 || s.QualityFlags.Count > 0) Console.WriteLine($"   tags=[{string.Join(",", s.Tags)}] flags=[{string.Join(",", s.QualityFlags)}]");
                Console.Write(string.Join('\n', lastLines));
                Console.WriteLine($"⟦{s.TargetText}⟧<EOL>");
                if (sem.TryGetValue(s.SampleId, out var r) && r.Prompt is { Length: > 0 })
                {
                    Console.WriteLine("   semantic prompt (" + r.Status + "):");
                    foreach (var l in r.Prompt.TrimEnd().Split('\n')) Console.WriteLine("   │ " + (l.Length > 200 ? l[..200] + "…" : l));
                    if (r.Leakage is { } lk) Console.WriteLine($"   target ids [{string.Join(",", lk.TargetIdentifiers)}] covered [{string.Join(",", lk.CoveredTargetIdentifiers)}] violations [{string.Join(",", lk.Violations)}]");
                }
                Console.WriteLine();
            }
            return 0;
        }

        public static int Render(string dataset, string outDir, PromptOptions o, int previewCount)
        {
            Directory.CreateDirectory(outDir);
            var sem = Jsonl.Find(dataset, "semantic.jsonl") is { } sp
                ? Jsonl.Read<SemanticRecord>(sp).Where(r => r.VisibilityPolicy == o.Policy).ToDictionary(r => r.SampleId)
                : new Dictionary<string, SemanticRecord>();
            var writers = new Dictionary<string, JsonlWriter<TrainingRecord>>();
            var stats = new Counters();
            long promptChars = 0, semChars = 0, n = 0;
            var preview = new List<(double Key, FlcSampleRecord S, TrainingRecord T)>();
            foreach (var s in Jsonl.Read<FlcSampleRecord>(Jsonl.Find(dataset, "samples.jsonl")!))
            {
                var t = PromptRenderer.Render(s, sem.GetValueOrDefault(s.SampleId), o);
                if (!writers.TryGetValue(s.Split, out var w)) writers[s.Split] = w = new JsonlWriter<TrainingRecord>(Path.Combine(outDir, $"prompts.{s.Split}.jsonl"));
                w.Write(t);
                n++; promptChars += t.Prompt.Length; semChars += t.SemanticChars;
                stats.Add("records." + s.Split);
                stats.Add("with_semantic", t.HasSemantic ? 1 : 0);
                stats.Add("code_truncated", t.CodeTruncated ? 1 : 0);
                stats.Add("semantic_items_dropped", t.SemanticItemsDropped);
                var key = Hashing.Uniform("preview", s.SampleId) - (t.HasSemantic ? 1 : 0); // semantic examples first
                preview.Add((key, s, t));
                if (preview.Count > previewCount * 4) preview = preview.OrderBy(x => x.Key).Take(previewCount).ToList();
            }
            var outputs = writers.Values.Select(w => w.Complete()).ToList();
            var summary = new Dictionary<string, object?>
            {
                ["prompt_format"] = o.Format, ["options"] = o, ["special_tokens"] = PromptRenderer.SpecialTokens, ["records"] = n,
                ["mean_prompt_chars"] = n == 0 ? 0 : Math.Round(promptChars / (double)n, 1),
                ["mean_semantic_chars"] = n == 0 ? 0 : Math.Round(semChars / (double)n, 1),
                ["counters"] = stats.Snapshot(),
                ["outputs"] = outputs.Select(x => x with { Path = Path.GetFileName(x.Path) }),
                ["note"] = "Budgets are in UTF-16 chars; no tokenizer is pinned yet, so token counts are not reported.",
            };
            File.WriteAllText(Path.Combine(outDir, "render-summary.json"), FlcJson.Serialize(summary, indented: true) + "\n");
            var md = new System.Text.StringBuilder($"# {o.Format} preview\n\nSpecial tokens are shown literally. Loss applies to COMPLETION only.\n\n");
            foreach (var (_, s, t) in preview.OrderBy(x => x.Key).Take(previewCount))
            {
                md.Append($"## {s.CaretKind}/{s.CaretSubkind} · `{s.RelativePath}:{s.CaretLineZeroBased + 1}:{s.CaretColumnUtf16ZeroBased + 1}` · semantic={s.SemanticStatus}\n\n");
                var p = t.Prompt.Length > 1800 ? "…" + t.Prompt[^1800..] : t.Prompt;
                md.Append("PROMPT:\n```text\n").Append(p).Append("\n```\nCOMPLETION:\n```text\n").Append(t.Completion).Append("\n```\n\n");
            }
            File.WriteAllText(Path.Combine(outDir, "preview.md"), md.ToString());
            Console.WriteLine(FlcJson.Serialize(summary, indented: true));
            return 0;
        }

        public static int SemanticCompare(string a, string b, string policy, int examples)
        {
            Dictionary<string, SemanticRecord> Load(string d) => Jsonl.Read<SemanticRecord>(Jsonl.Find(d, "semantic.jsonl")!)
                .Where(r => r.VisibilityPolicy == policy).ToDictionary(r => r.SampleId);
            var ra = Load(a); var rb = Load(b);
            var common = ra.Keys.Intersect(rb.Keys).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var diffs = new Counters();
            var shown = 0;
            foreach (var id in common)
            {
                var (x, y) = (ra[id], rb[id]);
                diffs.Add("compared");
                if (x.Prompt == y.Prompt) diffs.Add("identical_prompt");
                static string Set(IEnumerable<SymbolFact> f) => string.Join(",", f.Select(l => l.Name + ":" + l.Type).Order(StringComparer.Ordinal));
                bool same = true;
                void Check(string key, bool equal) { if (!equal) { diffs.Add(key); same = false; } }
                Check("status_differs", x.Status == y.Status);
                Check("receiver_differs", x.ReceiverType == y.ReceiverType);
                Check("expected_differs", x.ExpectedType == y.ExpectedType);
                Check("enclosing_differs", x.EnclosingSymbol == y.EnclosingSymbol);
                Check("locals_differ", Set(x.Locals) == Set(y.Locals));
                Check("parameters_differ", Set(x.Parameters) == Set(y.Parameters));
                Check("this_member_names_differ", x.ThisMembers.Select(m => m.Name).Order().SequenceEqual(y.ThisMembers.Select(m => m.Name).Order()));
                Check("member_names_differ", x.Members.Select(m => m.Name).Order().SequenceEqual(y.Members.Select(m => m.Name).Order()));
                Check("candidates_differ", x.InvocationCandidates.Select(c => c.Signature).Order().SequenceEqual(y.InvocationCandidates.Select(c => c.Signature).Order()));
                if (same) { diffs.Add("equivalent_facts"); continue; }
                diffs.Add("engine." + x.AnalysisEngine + "->" + y.AnalysisEngine);
                if (shown++ < examples)
                    Console.WriteLine($"── {id} {x.AnalysisEngine} vs {y.AnalysisEngine}\n--- a\n{x.Prompt}--- b\n{y.Prompt}");
            }
            Console.WriteLine(FlcJson.Serialize(diffs.Snapshot(), indented: true));
            return 0;
        }

        public static readonly string[] DeterministicOutputs =
            ["discovery.jsonl", "corpus.jsonl", "samples.jsonl", "exclusions.jsonl", "semantic.jsonl", "summary.json"];

        public static Dictionary<string, object> CompareDirs(string a, string b)
        {
            var result = new Dictionary<string, object>();
            foreach (var name in DeterministicOutputs)
            {
                var pa = Jsonl.Find(a, name) ?? Path.Combine(a, name);
                var pb = Jsonl.Find(b, name) ?? Path.Combine(b, name);
                if (!File.Exists(pa) && !File.Exists(pb)) continue;
                var ha = File.Exists(pa) ? Hashing.Sha256File(pa) : "missing";
                var hb = File.Exists(pb) ? Hashing.Sha256File(pb) : "missing";
                result[name] = new { identical = ha == hb, sha256_a = ha, sha256_b = hb };
            }
            return result;
        }

        public static int Compare(string a, string b)
        {
            var r = CompareDirs(a, b);
            Console.WriteLine(FlcJson.Serialize(r, indented: true));
            return r.Values.All(v => (bool)v.GetType().GetProperty("identical")!.GetValue(v)!) ? 0 : 4;
        }
    }

    /// <summary>Used when the workspace failed to load in best-effort mode: every lookup yields a reason code.</summary>
    sealed class NullDocumentSource(string reason) : ISemanticDocumentSource
    {
        public string Kind => "none";
        public Microsoft.CodeAnalysis.Document? Find(string relativePath, out string? r) { r = reason; return null; }
        public Task<bool> WarmAsync(Microsoft.CodeAnalysis.Project project, CancellationToken ct) => Task.FromResult(true);
        public Dictionary<string, object?> Describe() => new() { ["semantic_source"] = "none", ["reason"] = reason };
        public void Dispose() { }
    }
}
