using System.Diagnostics;
using FlcDataset.Core;
using FlcDataset.Extraction;

namespace FlcDataset.Semantics;

/// <summary>
/// Semantic stage. Each sample in the deterministic subset is analyzed once per configured visibility policy on an
/// immutable forked document (target removed). The editor_snapshot result sets the sample's semantic_status.
/// Failures are reason-coded; in best-effort mode the syntax sample is always kept.
/// </summary>
public sealed class SemanticEnricher(ISemanticDocumentSource source, DatasetConfig config, string mode) : ISemanticEnricher
{
    public string Mode => mode;
    public bool Required => mode == "semantic_required";

    public bool InSubset(FlcSampleRecord s) =>
        config.Semantic.SubsetFraction >= 1.0 || Hashing.Uniform(config.Seed.ToString(), "semantic_subset", s.SampleId) < config.Semantic.SubsetFraction;

    public async Task<(List<FlcSampleRecord> Samples, List<SemanticRecord> Records)> EnrichAsync(
        FileContext ctx, SourceDocument doc, List<FlcSampleRecord> samples, Counters counters, LatencyRecorder latency, CancellationToken ct)
    {
        var outSamples = new List<FlcSampleRecord>(samples.Count);
        var records = new List<SemanticRecord>();
        var document = source.Find(ctx.RelativePath, out var findReason);
        if (document is not null)
        {
            var text = (await document.GetTextAsync(ct)).ToString();
            if (text != doc.Text) { document = null; findReason = "document_text_mismatch"; }
        }
        FileSemanticContext? fileCtx = null;
        if (document is not null && samples.Any(InSubset))
        {
            var sw = Stopwatch.StartNew();
            var cached = await source.WarmAsync(document.Project, ct);
            counters.Add(cached ? "semantic.cache.project_compilation_hit" : "semantic.cache.project_compilation_miss");
            if (!cached) counters.Add("semantic.compile_warmup_ms", (long)sw.Elapsed.TotalMilliseconds);
            fileCtx = await FileSemanticContext.CreateAsync(document, ct);
        }

        foreach (var s in samples)
        {
            if (!InSubset(s))
            {
                outSamples.Add(s with { SemanticStatus = SemanticStatus.NotAttempted, SemanticReason = "not_in_subset" });
                continue;
            }
            counters.Add("semantic.attempted");
            if (document is null)
            {
                counters.Add("semantic.reason." + findReason);
                if (Required) { counters.Add("semantic.required_dropped"); continue; }
                counters.Add("semantic.status.syntax_fallback");
                outSamples.Add(s with { SemanticStatus = SemanticStatus.SyntaxFallback, SemanticReason = findReason });
                continue;
            }

            SemanticRecord? primary = null, fallback = null;
            foreach (var policy in config.Semantic.Policies)
            {
                var rec = await AnalyzeOne(fileCtx!, s, policy, counters, latency, ct);
                records.Add(rec);
                counters.Add($"semantic.{policy}.status.{rec.Status}");
                if (rec.Reason is not null) counters.Add($"semantic.{policy}.reason.{rec.Reason}");
                if (rec.Leakage is { } lk)
                {
                    counters.Add($"semantic.{policy}.target_identifiers", lk.TargetIdentifiers.Count);
                    counters.Add($"semantic.{policy}.covered_target_identifiers", lk.CoveredTargetIdentifiers.Count);
                    counters.Add($"semantic.{policy}.leak_violations", lk.Violations.Count);
                    if (lk.TargetIdentifiers.Count > 0 && lk.CoveredTargetIdentifiers.Count > 0) counters.Add($"semantic.{policy}.samples_with_coverage");
                }
                if (rec.ExpectedType is not null) counters.Add($"semantic.{policy}.has_expected_type");
                if (rec.ReceiverType is not null) counters.Add($"semantic.{policy}.has_receiver_type");
                if (rec.Locals.Count + rec.Parameters.Count > 0) counters.Add($"semantic.{policy}.has_scope_symbols");
                if (rec.Prompt is not null) counters.Add($"semantic.{policy}.prompt_chars", rec.Prompt.Length);
                if (policy == VisibilityPolicy.EditorSnapshot) primary = rec;
                fallback ??= rec;
            }
            primary ??= fallback!;
            if (Required && primary.Status is SemanticStatus.Failed) { counters.Add("semantic.required_dropped"); continue; }
            counters.Add("semantic.status." + primary.Status);
            outSamples.Add(s with { SemanticStatus = primary.Status, SemanticReason = primary.Reason });
        }
        return (outSamples, records);
    }

    async Task<SemanticRecord> AnalyzeOne(FileSemanticContext fileCtx, FlcSampleRecord s, string policy, Counters counters,
        LatencyRecorder latency, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(config.Semantic.TimeoutMs);
        var sw = Stopwatch.StartNew();
        try
        {
            var r = await SemanticAnalyzer.AnalyzeAsync(fileCtx, s, policy, config.Semantic, cts.Token);
            counters.Add($"semantic.engine.{r.AnalysisEngine}");
            return r;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(s, policy, "timeout");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            counters.Add("semantic.exception." + e.GetType().Name);
            if (Environment.GetEnvironmentVariable("FLC_SEMANTIC_DEBUG") == "1")
                Console.Error.WriteLine($"[semantic-exception] {s.RelativePath}:{s.CaretLineZeroBased + 1}:{s.CaretColumnUtf16ZeroBased + 1} {policy} {s.CaretKind}\n{e}");
            return Fail(s, policy, "exception:" + e.GetType().Name);
        }
        finally
        {
            latency.Record(sw.Elapsed);
        }
    }

    static SemanticRecord Fail(FlcSampleRecord s, string policy, string reason) => new()
    {
        SampleId = s.SampleId, VisibilityPolicy = policy, Status = SemanticStatus.Failed, Reason = reason, Project = s.Project,
    };
}
