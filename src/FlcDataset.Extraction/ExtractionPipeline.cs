using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using FlcDataset.Core;

namespace FlcDataset.Extraction;

/// <summary>Optional semantic stage, executed inside the bounded worker pool after syntax extraction of a file.</summary>
public interface ISemanticEnricher
{
    string Mode { get; }
    /// <summary>Returns the (possibly status-updated) samples plus semantic sidecar records. Must never throw for per-sample failures.</summary>
    Task<(List<FlcSampleRecord> Samples, List<SemanticRecord> Records)> EnrichAsync(
        FileContext ctx, SourceDocument doc, List<FlcSampleRecord> samples, Counters counters, LatencyRecorder latency, CancellationToken ct);
}

public sealed record PipelineOptions
{
    public required string RepoPath { get; init; }
    public required string OutputDir { get; init; }
    public required DatasetConfig Config { get; init; }
    public string Mode { get; init; } = "syntax_only";
    public int Workers { get; init; } = 1;
    public bool Gzip { get; init; }
    public bool WriteCorpus { get; init; } = true;
    /// <summary>False = E0 discovery/corpus only (no caret extraction).</summary>
    public bool ExtractSamples { get; init; } = true;
    public ISemanticEnricher? Enricher { get; init; }
    public RunLog Log { get; init; } = RunLog.Null;
    /// <summary>Extra timings measured before the pipeline (e.g. workspace load) to include in the manifest.</summary>
    public Dictionary<string, double> PreStageTimingsMs { get; init; } = new();
    public Dictionary<string, object?> SemanticEnvironment { get; init; } = new();
}

/// <summary>Deterministic content summary: identical input/config/seed must produce identical bytes.</summary>
public sealed record RunSummary
{
    public required string RepositoryId { get; init; }
    public required string? Revision { get; init; }
    public required string Mode { get; init; }
    public required string ConfigVersion { get; init; }
    public required string ConfigSha256 { get; init; }
    public required ulong Seed { get; init; }
    public required SortedDictionary<string, long> Files { get; init; }
    public required SortedDictionary<string, long> Lines { get; init; }
    public required SortedDictionary<string, long> Candidates { get; init; }
    public required SortedDictionary<string, long> Excluded { get; init; }
    public required SortedDictionary<string, long> Samples { get; init; }
    public required SortedDictionary<string, long> Semantic { get; init; }
    public required SortedDictionary<string, SortedDictionary<string, long>> SamplesByProject { get; init; }
    public required long AcceptedSourceBytes { get; init; }
    public required long AcceptedSourceChars { get; init; }
    public required long AcceptedSourceLines { get; init; }
    public required double DuplicateFraction { get; init; }
    public required double MeanTargetChars { get; init; }
}

public sealed record PipelineResult(RunSummary Summary, RunManifest Manifest, string OutputDir);

public sealed record RunManifest
{
    public string SchemaVersion { get; init; } = SchemaVersions.RunManifest;
    public required string RunId { get; init; }
    public required string Generator { get; init; }
    public required string StartedUtc { get; init; }
    public required string Mode { get; init; }
    public required int Workers { get; init; }
    public required RepositoryInfo Repository { get; init; }
    public required DatasetConfig Config { get; init; }
    public required string ConfigSha256 { get; init; }
    public required Dictionary<string, object?> Environment { get; init; }
    public required Dictionary<string, double> StageTimingsMs { get; init; }
    public required Dictionary<string, double> StageWorkerTimeMs { get; init; }
    public required Dictionary<string, LatencySummary> Latency { get; init; }
    public required ResourceSummary Resources { get; init; }
    public required Dictionary<string, double> Throughput { get; init; }
    public required List<OutputFileInfo> Outputs { get; init; }
    public required SortedDictionary<string, long> Counters { get; init; }
}

public static class ExtractionPipeline
{
    sealed record WorkItem(DiscoveredFile File, FileExtractionResult? Result, FileContext? Ctx, string? Error);

    public static Dictionary<string, object?> EnvironmentInfo() => new()
    {
        ["os"] = RuntimeInformation.OSDescription,
        ["arch"] = RuntimeInformation.OSArchitecture.ToString(),
        ["processors"] = Environment.ProcessorCount,
        ["total_memory_bytes"] = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        ["dotnet_runtime"] = RuntimeInformation.FrameworkDescription,
        ["roslyn_version"] = typeof(Microsoft.CodeAnalysis.SyntaxTree).Assembly.GetName().Version?.ToString(),
        ["roslyn_informational_version"] = typeof(Microsoft.CodeAnalysis.SyntaxTree).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion,
        ["server_gc"] = System.Runtime.GCSettings.IsServerGC,
        ["cpu_model"] = CpuModel(),
    };

    static string? CpuModel()
    {
        try
        {
            if (File.Exists("/proc/cpuinfo"))
                return File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name"))?.Split(':', 2)[1].Trim();
        }
        catch (IOException) { }
        return null;
    }

    public static async Task<PipelineResult> RunAsync(PipelineOptions o, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        using var sampler = new ResourceSampler();
        var timings = new StageTimings();
        foreach (var (k, v) in o.PreStageTimingsMs) timings.Add(k, v);
        var workerTime = new StageTimings();
        var counters = new Counters();
        var fileLatency = new LatencyRecorder();
        var semLatency = new LatencyRecorder();
        var cfg = o.Config;
        var log = o.Log;

        var repo = timings.Time("inspect_repository", () => RepositoryInfo.Inspect(o.RepoPath, cfg));
        log.Info("pipeline", "repository", new { repo.Root, repo.Revision, repo.Dirty, license = repo.License, repo.GlobalJsonSdk });
        if (repo.Revision is null) log.Warn("pipeline", "revision_unresolved", new { repo.RevisionError });
        if (repo.Dirty == true) log.Warn("pipeline", "dirty_checkout", new { note = "tracked files modified; revision does not identify content exactly" });

        var discoverer = new Discoverer(cfg, repo);
        var extractor = new CaretExtractor(cfg, discoverer.SecretPatterns);
        Directory.CreateDirectory(o.OutputDir);

        using var discoveryOut = new JsonlWriter<DiscoveryRecord>(Path.Combine(o.OutputDir, "discovery.jsonl"), o.Gzip);
        using var corpusOut = o.WriteCorpus ? new JsonlWriter<CorpusFileRecord>(Path.Combine(o.OutputDir, "corpus.jsonl"), o.Gzip) : null;
        using var samplesOut = new JsonlWriter<FlcSampleRecord>(Path.Combine(o.OutputDir, "samples.jsonl"), o.Gzip);
        using var exclusionsOut = new JsonlWriter<ExclusionRecord>(Path.Combine(o.OutputDir, "exclusions.jsonl"), o.Gzip);
        using var semanticOut = o.Enricher is null ? null : new JsonlWriter<SemanticRecord>(Path.Combine(o.OutputDir, "semantic.jsonl"), o.Gzip);

        var workers = Math.Max(1, o.Workers);
        var gate = new SemaphoreSlim(workers);
        // Bounded, order-preserving pipeline: at most 2*workers files are in flight; the writer consumes in discovery order.
        var channel = Channel.CreateBounded<Task<WorkItem>>(new BoundedChannelOptions(workers * 2) { SingleReader = true, SingleWriter = true });
        var discoverySw = new Stopwatch();
        var wall = Stopwatch.StartNew();

        var producer = Task.Run(async () =>
        {
            try
            {
                using var e = discoverer.Discover().GetEnumerator();
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    discoverySw.Start();
                    bool has = e.MoveNext();
                    discoverySw.Stop();
                    if (!has) break;
                    var file = e.Current;
                    await gate.WaitAsync(ct);
                    var task = Task.Run(() => ProcessFile(file), ct);
                    await channel.Writer.WriteAsync(task, ct);
                }
                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                channel.Writer.Complete(ex);
            }
        }, ct);

        // Stage 1 (parallel): syntax extraction only.
        WorkItem ProcessFile(DiscoveredFile file)
        {
            try
            {
                if (file.Document is null) return new WorkItem(file, null, null, null);
                var r = file.Record;
                var fctx = new FileContext(cfg.RepositoryId, repo.Revision, r.RelativePath, r.Project, r.IsTest, r.Split!, discoverer.SplitOf(r.RelativePath).Group);
                if (!o.ExtractSamples) return new WorkItem(file, new FileExtractionResult(), fctx, null);
                var sw = Stopwatch.StartNew();
                var result = extractor.Extract(file.Document, fctx);
                fileLatency.Record(sw.Elapsed);
                workerTime.Add("syntax_parse", result.ParseMs);
                workerTime.Add("syntax_extract", result.ExtractMs);
                return new WorkItem(file, result, fctx, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new WorkItem(file, null, null, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                gate.Release();
            }
        }

        var exclusionPerReason = new Dictionary<string, int>(StringComparer.Ordinal);
        var dupKeys = new HashSet<string>(StringComparer.Ordinal);
        var perProject = new Dictionary<string, int>(StringComparer.Ordinal);
        long admittedTotal = 0;
        var byProject = new SortedDictionary<string, SortedDictionary<string, long>>(StringComparer.Ordinal);
        long acceptedBytes = 0, acceptedChars = 0, acceptedLines = 0, targetChars = 0, duplicates = 0;
        var admitSw = new Stopwatch();
        var writeSw = new Stopwatch();

        // Stage 2 (single, ordered): discovery/corpus/exclusion output, dataset-level dedup and caps. Only admitted samples
        // reach the (expensive) semantic stage. Stage 3 (parallel, bounded): semantic enrichment. Stage 4 (single, ordered):
        // sample/semantic output. Both hand-offs are bounded channels of 2*workers tasks, so memory stays bounded.
        var semGate = new SemaphoreSlim(workers);
        var stage3 = Channel.CreateBounded<Task<(List<FlcSampleRecord> Samples, List<SemanticRecord> Semantic)>>(
            new BoundedChannelOptions(workers * 2) { SingleReader = true, SingleWriter = true });

        var admission = Task.Run(async () =>
        {
            try
            {
                await foreach (var task in channel.Reader.ReadAllAsync(ct))
                {
                    var item = await task;
                    var rec = item.File.Record;
                    admitSw.Start();
                    counters.Add("files.discovered");
                    counters.Add($"files.{rec.Status}");
                    if (rec.SkipReason is not null) counters.Add("files.skipped." + rec.SkipReason);
                    discoveryOut.Write(rec);
                    if (item.Error is not null)
                    {
                        counters.Add("files.extraction_error");
                        log.Error("extract", "file_failed", new { rec.RelativePath, item.Error });
                    }
                    var kept = new List<FlcSampleRecord>();
                    if (item.File.Document is { } doc && item.Result is { } res)
                    {
                        acceptedBytes += doc.ByteLength;
                        acceptedChars += doc.Text.Length;
                        acceptedLines += doc.Lines.Count;
                        if (rec.IsTest) counters.Add("files.accepted_test");
                        corpusOut?.Write(new CorpusFileRecord
                        {
                            RepositoryId = cfg.RepositoryId, Revision = repo.Revision, RelativePath = rec.RelativePath, Sha256 = doc.Sha256,
                            Bytes = doc.ByteLength, License = repo.License.Spdx, LicenseReason = repo.License.Reason, HasBom = doc.HasBom,
                            NewlineStyle = doc.NewlineStyle(), Project = rec.Project, IsTest = rec.IsTest, Split = rec.Split!,
                            Lines = doc.Lines.Count, Content = doc.Text,
                        });
                        foreach (var (k, v) in res.Counts) counters.Add(k, v);
                        if (res.SyntaxErrors > 0) counters.Add("files.with_syntax_errors");
                        foreach (var ex in res.Exclusions)
                        {
                            var n = exclusionPerReason.GetValueOrDefault(ex.Reason);
                            if (n < cfg.Sampling.ExclusionExamplesPerReason) exclusionsOut.Write(ex);
                            exclusionPerReason[ex.Reason] = n + 1;
                        }
                        foreach (var sample in res.Samples)
                        {
                            var trimmedLine = doc.Text[(sample.LineStartUtf16Offset + sample.Indentation.Length)..sample.TargetEndUtf16Offset];
                            var dupKey = Hashing.Sha256Hex(trimmedLine + "\u0001" + (sample.CaretUtf16Offset - sample.LineStartUtf16Offset - sample.Indentation.Length));
                            if (!dupKeys.Add(dupKey))
                            {
                                duplicates++;
                                if (cfg.Sampling.DropDuplicateLineTargets) { counters.Add("samples.dropped_duplicate"); continue; }
                            }
                            var keep = cfg.Sampling.KeepFraction * (sample.IsTest ? cfg.Sampling.TestKeepFraction : 1.0);
                            if (keep < 1.0 && Hashing.Uniform(cfg.Seed.ToString(), "keep", sample.SampleId) >= keep)
                            { counters.Add(sample.IsTest ? "samples.dropped_thinning_test" : "samples.dropped_thinning"); continue; }
                            if (cfg.Sampling.MaxSamplesPerRepo > 0 && admittedTotal >= cfg.Sampling.MaxSamplesPerRepo) { counters.Add("samples.dropped_repo_cap"); continue; }
                            var projKey = sample.Project ?? "(none)";
                            var pc = perProject.GetValueOrDefault(projKey);
                            if (cfg.Sampling.MaxSamplesPerProject > 0 && pc >= cfg.Sampling.MaxSamplesPerProject) { counters.Add("samples.dropped_project_cap"); continue; }
                            admittedTotal++;
                            perProject[projKey] = pc + 1;
                            kept.Add(sample);
                        }
                    }
                    admitSw.Stop();

                    Task<(List<FlcSampleRecord>, List<SemanticRecord>)> next;
                    if (o.Enricher is null || kept.Count == 0 || item.Ctx is null || item.File.Document is null)
                        next = Task.FromResult((kept, new List<SemanticRecord>()));
                    else
                    {
                        await semGate.WaitAsync(ct);
                        var (fctx, d) = (item.Ctx, item.File.Document);
                        next = Task.Run(async () =>
                        {
                            try
                            {
                                var ssw = Stopwatch.StartNew();
                                var r = await o.Enricher.EnrichAsync(fctx, d, kept, counters, semLatency, ct);
                                workerTime.Add("semantic", ssw.Elapsed.TotalMilliseconds);
                                return r;
                            }
                            finally { semGate.Release(); }
                        }, ct);
                    }
                    await stage3.Writer.WriteAsync(next, ct);
                }
                stage3.Writer.Complete();
            }
            catch (Exception ex)
            {
                stage3.Writer.Complete(ex);
            }
        }, ct);

        await foreach (var task in stage3.Reader.ReadAllAsync(ct))
        {
            var (samples, semantic) = await task;
            writeSw.Start();
            var semById = semantic.ToLookup(s => s.SampleId);
            foreach (var sample in samples)
            {
                samplesOut.Write(sample);
                foreach (var s in semById[sample.SampleId]) semanticOut?.Write(s);
                counters.Add("samples.written");
                counters.Add("samples.kind." + sample.CaretKind);
                counters.Add("samples.split." + sample.Split);
                counters.Add("samples.semantic_status." + sample.SemanticStatus);
                if (sample.IsTest) counters.Add("samples.test_code");
                foreach (var f in sample.QualityFlags) counters.Add("samples.flag." + f);
                targetChars += sample.TargetText.Length;
                var projKey = sample.Project ?? "(none)";
                if (!byProject.TryGetValue(projKey, out var pk)) byProject[projKey] = pk = new(StringComparer.Ordinal);
                pk[sample.CaretKind] = pk.GetValueOrDefault(sample.CaretKind) + 1;
                pk["_total"] = pk.GetValueOrDefault("_total") + 1;
            }
            writeSw.Stop();
        }
        await admission;
        await producer;
        wall.Stop();
        timings.Add("discovery_io", discoverySw.Elapsed.TotalMilliseconds);
        timings.Add("pipeline_wall", wall.Elapsed.TotalMilliseconds);
        timings.Add("writer_serialization", admitSw.Elapsed.TotalMilliseconds + writeSw.Elapsed.TotalMilliseconds);

        var finalizeSw = Stopwatch.StartNew();
        var outputs = new List<OutputFileInfo> { discoveryOut.Complete() };
        if (corpusOut is not null) outputs.Add(corpusOut.Complete());
        outputs.Add(samplesOut.Complete());
        outputs.Add(exclusionsOut.Complete());
        if (semanticOut is not null) outputs.Add(semanticOut.Complete());
        timings.Add("finalize_outputs", finalizeSw.Elapsed.TotalMilliseconds);

        long written = counters.Get("samples.written");
        var summary = new RunSummary
        {
            RepositoryId = cfg.RepositoryId, Revision = repo.Revision, Mode = o.Mode, ConfigVersion = cfg.ConfigVersion,
            ConfigSha256 = cfg.Hash(), Seed = cfg.Seed,
            Files = counters.Snapshot("files."), Lines = counters.Snapshot("lines."), Candidates = counters.Snapshot("candidates."),
            Excluded = counters.Snapshot("excluded."), Samples = counters.Snapshot("samples."),
            // Timing and cache counters depend on scheduling; they live only in run-manifest.json, never in the deterministic summary.
            Semantic = new(counters.Snapshot("semantic.").Where(kv => !kv.Key.StartsWith("cache.") && !kv.Key.EndsWith("_ms"))
                .ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal),
            SamplesByProject = byProject, AcceptedSourceBytes = acceptedBytes, AcceptedSourceChars = acceptedChars,
            AcceptedSourceLines = acceptedLines,
            DuplicateFraction = written + duplicates == 0 ? 0 : Math.Round(duplicates / (double)(written + duplicates), 6),
            MeanTargetChars = written == 0 ? 0 : Math.Round(targetChars / (double)written, 3),
        };
        var summaryPath = Path.Combine(o.OutputDir, "summary.json");
        await File.WriteAllTextAsync(summaryPath, FlcJson.Serialize(summary, indented: true) + "\n", ct);
        outputs.Add(new OutputFileInfo(summaryPath, 1, new FileInfo(summaryPath).Length, Hashing.Sha256File(summaryPath)));

        var resources = sampler.Stop();
        var wallSec = wall.Elapsed.TotalSeconds;
        var env = EnvironmentInfo();
        foreach (var (k, v) in o.SemanticEnvironment) env[k] = v;
        var manifest = new RunManifest
        {
            RunId = Hashing.StableId(cfg.Hash(), repo.Revision ?? "none", o.Mode, started.ToString("O")),
            Generator = SchemaVersions.GeneratorVersion,
            StartedUtc = started.ToString("O"),
            Mode = o.Mode,
            Workers = workers,
            Repository = repo,
            Config = cfg,
            ConfigSha256 = cfg.Hash(),
            Environment = env,
            StageTimingsMs = timings.Snapshot(),
            StageWorkerTimeMs = workerTime.Snapshot(),
            Latency = new() { ["file_syntax"] = fileLatency.Summarize(), ["sample_semantic"] = semLatency.Summarize() },
            Resources = resources,
            Throughput = new()
            {
                ["samples_per_sec"] = Math.Round(written / wallSec, 2),
                ["files_per_sec"] = Math.Round(counters.Get("files.accepted") / wallSec, 2),
                ["source_mib_per_sec"] = Math.Round(acceptedBytes / 1048576.0 / wallSec, 4),
                ["output_bytes"] = outputs.Sum(x => x.Bytes),
            },
            Outputs = outputs.Select(x => x with { Path = Path.GetFileName(x.Path) }).ToList(),
            Counters = counters.Snapshot(),
        };
        await File.WriteAllTextAsync(Path.Combine(o.OutputDir, "run-manifest.json"), FlcJson.Serialize(manifest, indented: true) + "\n", ct);
        log.Info("pipeline", "completed", new { samples = written, files = counters.Get("files.accepted"), wall_ms = Math.Round(wall.Elapsed.TotalMilliseconds, 1) });
        return new PipelineResult(summary, manifest, o.OutputDir);
    }
}
