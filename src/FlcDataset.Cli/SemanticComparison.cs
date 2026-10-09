using FlcDataset.Core;

namespace FlcDataset.Cli;

/// <summary>E4 analysis: compares editor_snapshot and strict_prefix facts on identical samples, plus source (adhoc vs msbuild) agreement.</summary>
public static class SemanticComparison
{
    public sealed record PolicyStats
    {
        public required long Records { get; init; }
        public required SortedDictionary<string, long> Status { get; init; }
        public required SortedDictionary<string, long> Reasons { get; init; }
        public required double MeanLocals { get; init; }
        public required double MeanParameters { get; init; }
        public required double MeanThisMembers { get; init; }
        public required double MeanReceiverMembers { get; init; }
        public required double HasReceiverRate { get; init; }
        public required double HasExpectedTypeRate { get; init; }
        public required double MeanPromptChars { get; init; }
        public required long TargetIdentifiers { get; init; }
        public required long CoveredTargetIdentifiers { get; init; }
        /// <summary>Fraction of identifiers in hidden targets that the payload names (upper bound on "helpfulness"; not leakage).</summary>
        public required double TargetIdentifierCoverage { get; init; }
        public required long LeakViolations { get; init; }
    }

    public static Dictionary<string, object?> Analyze(string datasetDir)
    {
        var kinds = Jsonl.Read<FlcSampleRecord>(Jsonl.Find(datasetDir, "samples.jsonl")!).ToDictionary(s => s.SampleId, s => s.CaretKind);
        var recs = Jsonl.Read<SemanticRecord>(Jsonl.Find(datasetDir, "semantic.jsonl")!).ToList();
        var byPolicy = recs.GroupBy(r => r.VisibilityPolicy).ToDictionary(g => g.Key, g => Stats(g.ToList()));

        var paired = recs.GroupBy(r => r.SampleId)
            .Select(g => (Id: g.Key, E: g.FirstOrDefault(r => r.VisibilityPolicy == VisibilityPolicy.EditorSnapshot), S: g.FirstOrDefault(r => r.VisibilityPolicy == VisibilityPolicy.StrictPrefix)))
            .Where(p => p.E is not null && p.S is not null).ToList();
        long identicalPrompt = 0, editorExtraCoverage = 0, strictExtraCoverage = 0, editorMoreSymbols = 0, strictMoreSymbols = 0, statusDiffers = 0;
        foreach (var (_, e, s) in paired)
        {
            if (e!.Prompt == s!.Prompt) identicalPrompt++;
            if (e.Status != s.Status) statusDiffers++;
            var ec = e.Leakage?.CoveredTargetIdentifiers.ToHashSet() ?? [];
            var sc = s.Leakage?.CoveredTargetIdentifiers.ToHashSet() ?? [];
            if (ec.Except(sc).Any()) editorExtraCoverage++;
            if (sc.Except(ec).Any()) strictExtraCoverage++;
            int Count(SemanticRecord r) => r.Locals.Count + r.Parameters.Count + r.ThisMembers.Count + r.Members.Count + r.InvocationCandidates.Count;
            if (Count(e) > Count(s)) editorMoreSymbols++;
            if (Count(s) > Count(e)) strictMoreSymbols++;
        }
        double F(long n) => paired.Count == 0 ? 0 : Math.Round(n / (double)paired.Count, 4);

        var byKind = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var g in recs.Where(r => kinds.ContainsKey(r.SampleId)).GroupBy(r => kinds[r.SampleId]))
        {
            var row = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var pg in g.GroupBy(r => r.VisibilityPolicy))
            {
                var st = Stats(pg.ToList());
                row[pg.Key] = new { records = st.Records, resolved_rate = Rate(st.Status.GetValueOrDefault(SemanticStatus.Resolved), st.Records), coverage = st.TargetIdentifierCoverage, has_receiver = st.HasReceiverRate, has_expected_type = st.HasExpectedTypeRate };
            }
            byKind[g.Key] = row;
        }

        return new Dictionary<string, object?>
        {
            ["dataset"] = Path.GetFileName(datasetDir),
            ["paired_samples"] = paired.Count,
            ["per_policy"] = byPolicy,
            ["identical_prompt_fraction"] = F(identicalPrompt),
            ["status_differs_fraction"] = F(statusDiffers),
            ["editor_has_more_symbols_fraction"] = F(editorMoreSymbols),
            ["strict_has_more_symbols_fraction"] = F(strictMoreSymbols),
            ["editor_only_target_coverage_fraction"] = F(editorExtraCoverage),
            ["strict_only_target_coverage_fraction"] = F(strictExtraCoverage),
            ["by_caret_kind"] = byKind,
        };
    }

    static double Rate(long n, long d) => d == 0 ? 0 : Math.Round(n / (double)d, 4);

    public static PolicyStats Stats(List<SemanticRecord> r)
    {
        double M(Func<SemanticRecord, double> f) => r.Count == 0 ? 0 : Math.Round(r.Average(f), 3);
        long tid = r.Sum(x => (long)(x.Leakage?.TargetIdentifiers.Count ?? 0));
        long cov = r.Sum(x => (long)(x.Leakage?.CoveredTargetIdentifiers.Count ?? 0));
        return new PolicyStats
        {
            Records = r.Count,
            Status = new(r.GroupBy(x => x.Status).ToDictionary(g => g.Key, g => (long)g.Count()), StringComparer.Ordinal),
            Reasons = new(r.Where(x => x.Reason is not null).GroupBy(x => x.Reason!).ToDictionary(g => g.Key, g => (long)g.Count()), StringComparer.Ordinal),
            MeanLocals = M(x => x.Locals.Count),
            MeanParameters = M(x => x.Parameters.Count),
            MeanThisMembers = M(x => x.ThisMembers.Count),
            MeanReceiverMembers = M(x => x.Members.Count),
            HasReceiverRate = M(x => x.ReceiverType is null ? 0 : 1),
            HasExpectedTypeRate = M(x => x.ExpectedType is null ? 0 : 1),
            MeanPromptChars = M(x => x.Prompt?.Length ?? 0),
            TargetIdentifiers = tid,
            CoveredTargetIdentifiers = cov,
            TargetIdentifierCoverage = Rate(cov, tid),
            LeakViolations = r.Sum(x => (long)(x.Leakage?.Violations.Count ?? 0)),
        };
    }

    /// <summary>Agreement between the safe adhoc source and the trusted MSBuild source on the same samples (editor_snapshot).</summary>
    public static Dictionary<string, object?> CompareSources(string adhocDir, string msbuildDir)
    {
        static Dictionary<string, SemanticRecord> Load(string d) => Jsonl.Read<SemanticRecord>(Jsonl.Find(d, "semantic.jsonl")!)
            .Where(r => r.VisibilityPolicy == VisibilityPolicy.EditorSnapshot).ToDictionary(r => r.SampleId);
        var a = Load(adhocDir);
        var m = Load(msbuildDir);
        var common = a.Keys.Intersect(m.Keys).ToList();
        long sameStatus = 0, sameReceiver = 0, bothReceiver = 0, sameExpected = 0, bothExpected = 0, samePrompt = 0;
        foreach (var id in common)
        {
            var x = a[id]; var y = m[id];
            if (x.Status == y.Status) sameStatus++;
            if (x.Prompt == y.Prompt) samePrompt++;
            if (y.ReceiverType is not null) { bothReceiver++; if (x.ReceiverType == y.ReceiverType) sameReceiver++; }
            if (y.ExpectedType is not null) { bothExpected++; if (x.ExpectedType == y.ExpectedType) sameExpected++; }
        }
        return new Dictionary<string, object?>
        {
            ["common_samples"] = common.Count,
            ["adhoc"] = Stats(common.Select(id => a[id]).ToList()),
            ["msbuild"] = Stats(common.Select(id => m[id]).ToList()),
            ["status_agreement"] = Rate(sameStatus, common.Count),
            ["identical_prompt_fraction"] = Rate(samePrompt, common.Count),
            ["receiver_type_agreement_where_msbuild_resolved"] = Rate(sameReceiver, bothReceiver),
            ["expected_type_agreement_where_msbuild_resolved"] = Rate(sameExpected, bothExpected),
        };
    }
}
