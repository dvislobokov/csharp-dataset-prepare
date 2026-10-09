namespace FlcDataset.Core;

/// <summary>Complete extraction configuration. Everything that affects output identity lives here and is hashed into the run manifest.</summary>
public sealed record DatasetConfig
{
    public string ConfigVersion { get; init; } = "default/1";
    public string RepositoryId { get; init; } = "local/unknown";
    public ulong Seed { get; init; } = 1;
    public LicenseConfig License { get; init; } = new();
    public DiscoveryConfig Discovery { get; init; } = new();
    public SamplingConfig Sampling { get; init; } = new();
    public ContextConfig Context { get; init; } = new();
    public SplitConfig Split { get; init; } = new();
    public SemanticConfig Semantic { get; init; } = new();
    public SecretsConfig Secrets { get; init; } = new();

    public static DatasetConfig Load(string path) => FlcJson.Deserialize<DatasetConfig>(File.ReadAllText(path));

    public string Hash() => Hashing.Sha256Hex(FlcJson.Serialize(this));
}

public sealed record LicenseConfig
{
    /// <summary>SPDX ids allowed to enter the training corpus.</summary>
    public List<string> Allowlist { get; init; } = ["MIT", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "0BSD", "Unlicense", "ISC"];
    /// <summary>Optional declared license; still verified against the repository LICENSE file when present.</summary>
    public string? Declared { get; init; }
    /// <summary>When true, files from repositories with unknown/non-allowlisted license are kept but flagged; otherwise skipped.</summary>
    public bool AllowUnknown { get; init; }
}

public sealed record DiscoveryConfig
{
    public List<string> Include { get; init; } = ["**/*.cs"];
    public List<string> Exclude { get; init; } = ["**/obj/**", "**/bin/**", "**/.git/**"];
    public List<string> GeneratedPatterns { get; init; } =
        ["**/*.g.cs", "**/*.g.i.cs", "**/*.Designer.cs", "**/*.generated.cs", "**/*.AssemblyInfo.cs", "**/Migrations/**", "**/*ModelSnapshot.cs"];
    public List<string> VendoredPatterns { get; init; } = ["**/vendor/**", "**/third_party/**", "**/external/**", "**/wwwroot/lib/**"];
    public List<string> TestPatterns { get; init; } = ["tests/**", "test/**", "**/*.Tests/**", "**/*.UnitTests/**", "**/*.FunctionalTests/**", "**/*Tests.cs"];
    public long MaxFileBytes { get; init; } = 1 << 20;
    /// <summary>Files containing any line longer than this are considered minified/generated-like and skipped.</summary>
    public int MaxFileLineChars { get; init; } = 2000;
}

public sealed record SamplingConfig
{
    /// <summary>Per-candidate acceptance probability by caret kind. Missing kind = 0 (disabled).</summary>
    public Dictionary<string, double> Weights { get; init; } = new()
    {
        ["line_start"] = 0.7,
        ["identifier_partial"] = 0.035,
        ["member_access"] = 0.8,
        ["argument_list"] = 0.5,
        ["after_keyword"] = 0.5,
        ["after_operator"] = 0.4,
        ["control_flow"] = 0.6,
        ["lambda_body"] = 0.5,
        ["linq"] = 0.6,
        ["log_message"] = 1.0,
        ["token_boundary"] = 0.02,
    };
    public int MaxSamplesPerLine { get; init; } = 2;
    /// <summary>Drop later samples whose (trimmed line, caret column within it) already occurred in this run (boilerplate like usings).</summary>
    public bool DropDuplicateLineTargets { get; init; } = true;
    public int MaxSamplesPerFile { get; init; } = 300;
    /// <summary>0 = unlimited.</summary>
    public int MaxSamplesPerProject { get; init; }
    /// <summary>Identifier internal offsets considered per identifier (1..N typed chars, plus camel-hump boundaries).</summary>
    public int IdentifierPrefixMax { get; init; } = 4;
    public int MaxLineChars { get; init; } = 240;
    public int MinTargetChars { get; init; } = 1;
    public int MaxTargetChars { get; init; } = 200;
    /// <summary>Targets made only of punctuation like "}", ");" are mostly auto-inserted by the IDE.</summary>
    public double TrivialTargetKeepProbability { get; init; } = 0.05;
    /// <summary>Max examples per exclusion reason written to exclusions.jsonl for audit.</summary>
    public int ExclusionExamplesPerReason { get; init; } = 25;
}

public sealed record ContextConfig
{
    public int LeftChars { get; init; } = 6000;
    public int RightChars { get; init; } = 1500;
}

public sealed record SplitConfig
{
    /// <summary>
    /// Group unit for split assignment: "project_family" (project name matched by <see cref="FamilyPattern"/>, so a service's
    /// API/Domain/Infrastructure/tests stay together), "project" or "file". Splits are assigned before caret expansion.
    /// </summary>
    public string GroupBy { get; init; } = "project_family";
    /// <summary>Regex over the project file name; capture group 1 is the family key.</summary>
    public string FamilyPattern { get; init; } = @"^(?:eShop\.)?([^.]+)";
    public double EvalFraction { get; init; } = 0.15;
    public double TestFraction { get; init; } = 0.0;
    /// <summary>Explicit overrides: group key -> split.</summary>
    public Dictionary<string, string> Overrides { get; init; } = new();
}

public sealed record SemanticConfig
{
    /// <summary>Solution, solution filter (.slnf) or project, relative to the repository, for trusted MSBuild loading.</summary>
    public string? Solution { get; init; }
    public int MaxScopeSymbols { get; init; } = 48;
    public int MaxMembers { get; init; } = 48;
    public int MaxThisMembers { get; init; } = 48;
    /// <summary>TYPE block: max nearby project types and members shown per type (0 disables the block).</summary>
    public int MaxContextTypes { get; init; } = 6;
    public int MaxTypeMembers { get; init; } = 10;
    /// <summary>Also consider types from referenced assemblies (framework/NuGet); default only types with source in the repository.</summary>
    public bool ContextTypesIncludeMetadata { get; init; }
    public int TimeoutMs { get; init; } = 10000;
    /// <summary>Deterministic hash-selected fraction of samples to enrich (E2 subset). 1.0 = all eligible samples (E3).</summary>
    public double SubsetFraction { get; init; } = 0.1;
    public List<string> Policies { get; init; } = ["editor_snapshot", "strict_prefix"];
    /// <summary>"auto": speculative binding of the enclosing member when possible, otherwise a document fork; "fork": always fork.</summary>
    public string Engine { get; init; } = "auto";
}

public sealed record SecretsConfig
{
    /// <summary>High-confidence credential formats: the whole file is skipped from the corpus (secret_detected).</summary>
    public List<string> Patterns { get; init; } =
    [
        @"-----BEGIN [A-Z ]*PRIVATE KEY-----",
        @"\bAKIA[0-9A-Z]{16}\b",
        @"\bgh[pousr]_[A-Za-z0-9]{36,}\b",
        @"\bxox[baprs]-[A-Za-z0-9-]{10,}",
        @"(?i)AccountKey=[A-Za-z0-9+/=]{20,}",
        @"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}",
    ];

    /// <summary>
    /// Generic credential-looking assignments: only the affected lines are excluded from samples (excluded.secret_detected).
    /// Require a literal value (quoted, or inside a connection string) so identifier assignments like `Password = password` pass.
    /// </summary>
    public List<string> LinePatterns { get; init; } =
    [
        // Values that look like environment-variable names (UPPER_SNAKE) or format templates are not secrets.
        @"(?i)\b\w*(password|passwd|pwd|secret|apikey|api_key|accesstoken|access_token)\w*""?\s*[:=]\s*@?""(?-i:(?![A-Z0-9_]+""))[^""\s{}]{8,}""",
        @"(?i)\b(password|pwd)=[^;""'\s]{6,};",
    ];
}
