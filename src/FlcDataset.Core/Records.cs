namespace FlcDataset.Core;

public static class SchemaVersions
{
    public const string CorpusFile = "corpus-file/v1";
    public const string DiscoveryFile = "discovery-file/v1";
    public const string FlcSample = "flc-sample/v1";
    public const string Semantic = "flc-semantic/v1";
    public const string Exclusion = "flc-exclusion/v1";
    public const string RunManifest = "run-manifest/v1";
    public const string GeneratorVersion = "flc-dataset/0.1.0";
}

/// <summary>Every discovered file with its decision. Written for accepted and skipped files.</summary>
public sealed record DiscoveryRecord
{
    public string SchemaVersion { get; init; } = SchemaVersions.DiscoveryFile;
    public required string RelativePath { get; init; }
    public required string Status { get; init; }          // accepted | skipped
    public string? SkipReason { get; init; }
    public string? MatchedPattern { get; init; }
    public long Bytes { get; init; }
    public string? Sha256 { get; init; }
    public string? Project { get; init; }
    public string? Split { get; init; }
    public bool IsTest { get; init; }
    public bool IsGenerated { get; init; }
    public string? GeneratedReason { get; init; }
    public string? DuplicateOf { get; init; }
    public int Lines { get; init; }
    public int Chars { get; init; }
}

/// <summary>Product A: one record per accepted source file and revision; content is exact (minus BOM, recorded separately).</summary>
public sealed record CorpusFileRecord
{
    public string SchemaVersion { get; init; } = SchemaVersions.CorpusFile;
    public required string RepositoryId { get; init; }
    public required string? Revision { get; init; }
    public required string RelativePath { get; init; }
    public string Language { get; init; } = "csharp";
    public required string Sha256 { get; init; }
    public required long Bytes { get; init; }
    public required string? License { get; init; }
    public string? LicenseReason { get; init; }
    public string Encoding { get; init; } = "utf-8";
    public required bool HasBom { get; init; }
    public required string NewlineStyle { get; init; }
    public required string? Project { get; init; }
    public required bool IsTest { get; init; }
    public required string Split { get; init; }
    public required int Lines { get; init; }
    /// <summary>Exact decoded text. Original bytes = (has_bom ? EF BB BF : "") + UTF8(content).</summary>
    public required string Content { get; init; }
    public bool ContentNormalized { get; init; }
}

/// <summary>Product B: caret-based full-line completion sample. All *_utf16 offsets index the decoded file text (BOM excluded).</summary>
public sealed record FlcSampleRecord
{
    public string SchemaVersion { get; init; } = SchemaVersions.FlcSample;
    public required string SampleId { get; init; }
    public required string RepositoryId { get; init; }
    public required string? Revision { get; init; }
    public required string RelativePath { get; init; }
    public required string? Project { get; init; }
    public required bool IsTest { get; init; }
    public required string SourceSha256 { get; init; }
    public required int CaretUtf16Offset { get; init; }
    public required int CaretLineZeroBased { get; init; }
    public required int CaretColumnUtf16ZeroBased { get; init; }
    public required long CaretByteOffset { get; init; }
    public required int TargetEndUtf16Offset { get; init; }
    public required long TargetEndByteOffset { get; init; }
    public required int LineStartUtf16Offset { get; init; }
    public required int LineEndUtf16Offset { get; init; }
    public required string CaretKind { get; init; }
    public required string? CaretSubkind { get; init; }
    public required List<string> Tags { get; init; }
    public required int LeftContextStartUtf16Offset { get; init; }
    public required bool LeftContextTruncated { get; init; }
    public required string LeftContext { get; init; }
    /// <summary>Exact missing suffix of the physical line, no line break, trailing whitespace excluded (goes to right_context).</summary>
    public required string TargetText { get; init; }
    public required string RightContext { get; init; }
    public required int RightContextEndUtf16Offset { get; init; }
    public required bool RightContextTruncated { get; init; }
    public required string EndOfLine { get; init; }
    public required string Indentation { get; init; }
    public required List<string> QualityFlags { get; init; }
    public required string Split { get; init; }
    public required string SplitGroup { get; init; }
    public required string SemanticStatus { get; init; }
    public required string? SemanticReason { get; init; }
    public required string ConfigVersion { get; init; }
    public required string ConfigSha256 { get; init; }
    public string Generator { get; init; } = SchemaVersions.GeneratorVersion;
}

/// <summary>Audit record for a caret candidate that was excluded by a negative stratum or quality rule.</summary>
public sealed record ExclusionRecord
{
    public string SchemaVersion { get; init; } = SchemaVersions.Exclusion;
    public required string RelativePath { get; init; }
    public required int CaretUtf16Offset { get; init; }
    public required int CaretLineZeroBased { get; init; }
    public required string Reason { get; init; }
    public required string CandidateKind { get; init; }
    public required string LineText { get; init; }
}
