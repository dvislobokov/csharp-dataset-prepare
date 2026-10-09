using System.Collections.Concurrent;
using System.Text.Json;
using FlcDataset.Core;
using FlcDataset.Extraction;
using Json.Schema;

namespace FlcDataset.Cli;

public sealed record ValidationReport
{
    public required string Dataset { get; init; }
    public required bool Ok { get; init; }
    public required SortedDictionary<string, long> Checked { get; init; }
    public required SortedDictionary<string, long> Failures { get; init; }
    public required List<string> FailureExamples { get; init; }
}

/// <summary>
/// Validates a dataset directory against schemas and against the original repository bytes:
/// exact reconstruction, offsets/line/column/byte round trips, id stability, split isolation, checksums and leakage audit.
/// </summary>
public sealed class DatasetValidator
{
    static readonly ConcurrentDictionary<string, JsonSchema> Schemas = new();
    readonly SortedDictionary<string, long> _checked = new(StringComparer.Ordinal);
    readonly SortedDictionary<string, long> _failures = new(StringComparer.Ordinal);
    readonly List<string> _examples = [];
    readonly Dictionary<string, SourceDocument?> _sources = new(StringComparer.Ordinal);

    public static string SchemaDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "schemas");

    static JsonSchema Schema(string file) => Schemas.GetOrAdd(file, f => JsonSchema.FromText(File.ReadAllText(Path.Combine(SchemaDirectory, f))));

    void Check(string name) => _checked[name] = _checked.GetValueOrDefault(name) + 1;

    void Fail(string name, string detail)
    {
        _failures[name] = _failures.GetValueOrDefault(name) + 1;
        if (_examples.Count < 50) _examples.Add($"{name}: {detail}");
    }

    bool ValidateSchema(string schemaFile, string line, string what)
    {
        Check("schema." + what);
        using var doc = JsonDocument.Parse(line);
        var r = Schema(schemaFile).Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (r.IsValid) return true;
        var errs = r.Details?.Where(d => d.Errors is { Count: > 0 })
            .Select(d => d.InstanceLocation + " " + string.Join(",", d.Errors!.Values)).Take(3) ?? [];
        Fail("schema." + what, string.Join("; ", errs) + " :: " + (line.Length > 160 ? line[..160] : line));
        return false;
    }

    SourceDocument? Source(string repo, string rel)
    {
        if (_sources.TryGetValue(rel, out var d)) return d;
        var full = Path.Combine(repo, rel);
        d = File.Exists(full) ? SourceDocument.TryDecode(File.ReadAllBytes(full), out _) : null;
        if (_sources.Count > 2000) _sources.Clear(); // bounded cache
        _sources[rel] = d;
        return d;
    }

    public ValidationReport Validate(string datasetDir, string? repoPath)
    {
        // 1. Manifest checksums.
        var manifestPath = Path.Combine(datasetDir, "run-manifest.json");
        string? expectedRevision = null;
        if (File.Exists(manifestPath))
        {
            var mtext = File.ReadAllText(manifestPath);
            ValidateSchema("run-manifest.v1.schema.json", mtext, "run_manifest");
            using var m = JsonDocument.Parse(mtext);
            expectedRevision = m.RootElement.GetProperty("repository").GetProperty("revision").GetString();
            foreach (var o in m.RootElement.GetProperty("outputs").EnumerateArray())
            {
                var p = Path.Combine(datasetDir, o.GetProperty("path").GetString()!);
                Check("manifest.output_checksum");
                if (!File.Exists(p)) { Fail("manifest.output_missing", p); continue; }
                if (Hashing.Sha256File(p) != o.GetProperty("sha256").GetString()) Fail("manifest.output_checksum", p);
            }
            if (Directory.EnumerateFiles(datasetDir, "*.partial").Any()) Fail("manifest.partial_files_present", datasetDir);
        }
        else Fail("manifest.missing", manifestPath);

        if (repoPath is not null && expectedRevision is not null)
        {
            Check("repository.revision");
            var (head, _) = RepositoryInfo.Git(Path.GetFullPath(repoPath), "rev-parse", "HEAD");
            if (head != expectedRevision) Fail("repository.revision", $"dataset={expectedRevision} repo={head}");
        }

        // 2. Corpus.
        var corpusSplit = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Jsonl.Find(datasetDir, "corpus.jsonl") is { } corpus)
        {
            foreach (var line in Jsonl.ReadLines(corpus))
            {
                if (!ValidateSchema("corpus-file.v1.schema.json", line, "corpus")) continue;
                var r = FlcJson.Deserialize<CorpusFileRecord>(line);
                Check("corpus.byte_reconstruction");
                if (Hashing.Sha256Hex(SourceDocument.ToBytes(r.Content, r.HasBom)) != r.Sha256) Fail("corpus.byte_reconstruction", r.RelativePath);
                if (repoPath is not null)
                {
                    Check("corpus.matches_repository");
                    var full = Path.Combine(repoPath, r.RelativePath);
                    if (!File.Exists(full) || Hashing.Sha256File(full) != r.Sha256) Fail("corpus.matches_repository", r.RelativePath);
                }
                corpusSplit[r.RelativePath] = r.Split;
            }
        }

        // 3. Samples.
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var groupSplit = new Dictionary<string, string>(StringComparer.Ordinal);
        var samplesPath = Jsonl.Find(datasetDir, "samples.jsonl");
        if (samplesPath is null) Fail("samples.missing", datasetDir);
        else
        {
            foreach (var line in Jsonl.ReadLines(samplesPath))
            {
                if (!ValidateSchema("flc-sample.v1.schema.json", line, "sample")) continue;
                var s = FlcJson.Deserialize<FlcSampleRecord>(line);
                Check("sample.unique_id");
                if (!ids.Add(s.SampleId)) Fail("sample.unique_id", s.SampleId);
                Check("sample.stable_id");
                var expectedId = Hashing.StableId(s.RepositoryId, s.RelativePath, s.SourceSha256, s.CaretUtf16Offset.ToString(), s.TargetEndUtf16Offset.ToString());
                if (expectedId != s.SampleId) Fail("sample.stable_id", s.SampleId);
                Check("sample.target_single_line");
                if (s.TargetText.Any(SourceDocument.IsLineBreakChar)) Fail("sample.target_single_line", s.SampleId);
                Check("sample.split_isolation");
                if (groupSplit.TryGetValue(s.SplitGroup, out var gs) && gs != s.Split) Fail("sample.split_isolation", $"{s.SplitGroup}: {gs} vs {s.Split}");
                groupSplit[s.SplitGroup] = s.Split;
                if (corpusSplit.TryGetValue(s.RelativePath, out var cs) && cs != s.Split) Fail("sample.split_matches_corpus", s.SampleId);
                if (s.Revision != expectedRevision) Fail("sample.revision_matches_manifest", s.SampleId);
                if (repoPath is not null) ValidateAgainstSource(repoPath, s);
            }
        }

        // 4. Semantic sidecar.
        if (Jsonl.Find(datasetDir, "semantic.jsonl") is { } sem)
        {
            foreach (var line in Jsonl.ReadLines(sem))
            {
                if (!ValidateSchema("flc-semantic.v1.schema.json", line, "semantic")) continue;
                var r = FlcJson.Deserialize<SemanticRecord>(line);
                Check("semantic.sample_exists");
                if (!ids.Contains(r.SampleId)) Fail("semantic.sample_exists", r.SampleId);
                Check("semantic.no_leak_violations");
                if (r.Leakage is { Violations.Count: > 0 }) Fail("semantic.no_leak_violations", $"{r.SampleId}: {string.Join(",", r.Leakage.Violations)}");
            }
        }

        return new ValidationReport
        {
            Dataset = Path.GetFullPath(datasetDir), Ok = _failures.Count == 0, Checked = _checked, Failures = _failures, FailureExamples = _examples,
        };
    }

    void ValidateAgainstSource(string repo, FlcSampleRecord s)
    {
        var doc = Source(repo, s.RelativePath);
        Check("source.exists_and_hash");
        if (doc is null || doc.Sha256 != s.SourceSha256) { Fail("source.exists_and_hash", s.RelativePath); return; }
        var t = doc.Text;
        Check("source.reconstruction");
        if (s.TargetEndUtf16Offset > t.Length || s.CaretUtf16Offset > s.TargetEndUtf16Offset
            || string.CompareOrdinal(t, s.CaretUtf16Offset, s.TargetText, 0, s.TargetText.Length) != 0
            || s.TargetText.Length != s.TargetEndUtf16Offset - s.CaretUtf16Offset)
        { Fail("source.reconstruction", s.SampleId); return; }
        var rebuilt = string.Concat(t.AsSpan(0, s.CaretUtf16Offset), s.TargetText, t.AsSpan(s.TargetEndUtf16Offset));
        if (rebuilt != t) Fail("source.reconstruction", s.SampleId);
        Check("source.left_context");
        if (t[s.LeftContextStartUtf16Offset..s.CaretUtf16Offset] != s.LeftContext) Fail("source.left_context", s.SampleId);
        if (s.LeftContextTruncated != (s.LeftContextStartUtf16Offset > 0)) Fail("source.left_context_truncated_flag", s.SampleId);
        Check("source.right_context");
        if (t[s.TargetEndUtf16Offset..s.RightContextEndUtf16Offset] != s.RightContext) Fail("source.right_context", s.SampleId);
        Check("source.line_column");
        var (line, col) = doc.LineColumn(s.CaretUtf16Offset);
        if (line != s.CaretLineZeroBased || col != s.CaretColumnUtf16ZeroBased) Fail("source.line_column", s.SampleId);
        var li = doc.Lines[line];
        if (li.Start != s.LineStartUtf16Offset || li.End != s.LineEndUtf16Offset) Fail("source.line_bounds", s.SampleId);
        Check("source.target_ends_at_line_content_end");
        var rest = t[s.TargetEndUtf16Offset..li.End];
        if (rest.Any(c => !char.IsWhiteSpace(c)) || (s.TargetText.Length > 0 && char.IsWhiteSpace(s.TargetText[^1])))
            Fail("source.target_ends_at_line_content_end", s.SampleId);
        Check("source.byte_offsets");
        if (doc.ByteOffset(s.CaretUtf16Offset) != s.CaretByteOffset || doc.ByteOffset(s.TargetEndUtf16Offset) != s.TargetEndByteOffset)
            Fail("source.byte_offsets", s.SampleId);
        Check("source.no_split_surrogate");
        if (s.CaretUtf16Offset > 0 && s.CaretUtf16Offset < t.Length && char.IsLowSurrogate(t[s.CaretUtf16Offset]) && char.IsHighSurrogate(t[s.CaretUtf16Offset - 1]))
            Fail("source.no_split_surrogate", s.SampleId);
    }
}
