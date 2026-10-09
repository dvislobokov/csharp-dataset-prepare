namespace FlcDataset.Core;

public static class SemanticStatus
{
    public const string NotAttempted = "not_attempted";
    public const string Resolved = "resolved";
    public const string PartiallyResolved = "partially_resolved";
    public const string SyntaxFallback = "syntax_fallback";
    public const string Failed = "failed";
}

public static class VisibilityPolicy
{
    /// <summary>Document = file with the target suffix removed; code after the line stays (editor-equivalent).</summary>
    public const string EditorSnapshot = "editor_snapshot";
    /// <summary>Document = only the text before the caret; nothing after the caret can influence facts.</summary>
    public const string StrictPrefix = "strict_prefix";
}

public sealed record SymbolFact
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public string? Type { get; init; }
    /// <summary>Declared nullable annotation (annotated/not_annotated/none). Not a flow-state claim.</summary>
    public string? NullableAnnotation { get; init; }
    public string? Signature { get; init; }
    public bool IsStatic { get; init; }
    public int Overloads { get; init; } = 1;
    public bool IsExtension { get; init; }
}

public sealed record InvocationCandidate
{
    public required string Signature { get; init; }
    public int ArgumentIndex { get; init; }
    public string? ParameterName { get; init; }
    public string? ParameterType { get; init; }
}

public sealed record LeakageAudit
{
    /// <summary>Identifiers occurring in the hidden target.</summary>
    public required List<string> TargetIdentifiers { get; init; }
    /// <summary>Target identifiers that the semantic payload also mentions (legitimate if they exist independently).</summary>
    public required List<string> CoveredTargetIdentifiers { get; init; }
    /// <summary>Payload symbols declared in this document whose name is absent from the analyzed snapshot text. Must be empty.</summary>
    public required List<string> Violations { get; init; }
}

public sealed record SemanticRecord
{
    public string SchemaVersion { get; init; } = SchemaVersions.Semantic;
    public required string SampleId { get; init; }
    public required string VisibilityPolicy { get; init; }
    /// <summary>speculative (enclosing member re-bound against the original compilation) or fork (whole-document snapshot).</summary>
    public string? AnalysisEngine { get; init; }
    public required string Status { get; init; }
    public string? Reason { get; init; }
    public string? Project { get; init; }
    public string? EnclosingSymbol { get; init; }
    public string? EnclosingKind { get; init; }
    public string? EnclosingType { get; init; }
    public string? ReturnType { get; init; }
    public string? ExpectedType { get; init; }
    public string? ExpectedTypeSource { get; init; }
    public List<SymbolFact> Locals { get; init; } = [];
    public List<SymbolFact> Parameters { get; init; } = [];
    public List<SymbolFact> ThisMembers { get; init; } = [];
    public string? ReceiverType { get; init; }
    public string? ReceiverKind { get; init; }
    public List<SymbolFact> Members { get; init; } = [];
    public List<InvocationCandidate> InvocationCandidates { get; init; } = [];
    public int SnapshotSyntaxErrors { get; init; }
    /// <summary>strict_prefix only: synthetic closing braces derived from the prefix alone (never from text after the caret).</summary>
    public string? SyntheticSuffix { get; init; }
    public bool Truncated { get; init; }
    /// <summary>Symbols dropped because they only exist through error recovery of code after the caret.</summary>
    public int DroppedRecoveryArtifacts { get; init; }
    /// <summary>Compact serialized prompt form, derived only from the structured facts above.</summary>
    public string? Prompt { get; init; }
    public LeakageAudit? Leakage { get; init; }
}
