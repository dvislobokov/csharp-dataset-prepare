using FlcDataset.Core;
using FlcDataset.Extraction;

namespace FlcDataset.Tests;

static class TestUtil
{
    public static string FixtureRepo => Path.Combine(AppContext.BaseDirectory, "fixtures", "repo1");

    /// <summary>Config with every caret kind enabled at probability 1 and no caps, so tests see all eligible carets.</summary>
    public static DatasetConfig AllCarets(Action<SamplingConfig>? _ = null) => new DatasetConfig
    {
        RepositoryId = "fixture/repo1",
        Seed = 7,
        Discovery = new DiscoveryConfig { Include = ["src/**/*.cs", "tests/**/*.cs"] },
        Sampling = new SamplingConfig
        {
            Weights = new()
            {
                ["line_start"] = 1, ["identifier_partial"] = 1, ["member_access"] = 1, ["argument_list"] = 1, ["after_keyword"] = 1,
                ["after_operator"] = 1, ["control_flow"] = 1, ["lambda_body"] = 1, ["linq"] = 1, ["log_message"] = 1, ["token_boundary"] = 1,
            },
            MaxSamplesPerLine = 1000, MaxSamplesPerFile = 100000, TrivialTargetKeepProbability = 1, DropDuplicateLineTargets = false,
        },
        Split = new SplitConfig { EvalFraction = 0.5 },
    };

    public static FileContext Ctx(string path = "src/X.cs") => new("fixture/repo1", null, path, "src/X.csproj", false, "train", "X");

    public static FileExtractionResult Extract(string text, DatasetConfig? cfg = null, bool bom = false) =>
        new CaretExtractor(cfg ?? AllCarets()).Extract(SourceDocument.FromText(text, bom), Ctx());

    public static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "flc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Hand-built sample at a caret: target = rest of line content (trailing whitespace excluded).</summary>
    public static FlcSampleRecord SampleAt(SourceDocument doc, string path, int caret, string kind = "line_start")
    {
        var line = doc.Lines[doc.LineIndexOf(caret)];
        int te = line.End;
        while (te > caret && char.IsWhiteSpace(doc.Text[te - 1])) te--;
        var (ln, col) = doc.LineColumn(caret);
        return new FlcSampleRecord
        {
            SampleId = Hashing.StableId("fixture/repo1", path, doc.Sha256, caret.ToString(), te.ToString()),
            RepositoryId = "fixture/repo1", Revision = null, RelativePath = path, Project = null, IsTest = false, SourceSha256 = doc.Sha256,
            CaretUtf16Offset = caret, CaretLineZeroBased = ln, CaretColumnUtf16ZeroBased = col, CaretByteOffset = doc.ByteOffset(caret),
            TargetEndUtf16Offset = te, TargetEndByteOffset = doc.ByteOffset(te), LineStartUtf16Offset = line.Start, LineEndUtf16Offset = line.End,
            CaretKind = kind, CaretSubkind = null, Tags = [], LeftContextStartUtf16Offset = 0, LeftContextTruncated = false,
            LeftContext = doc.Text[..caret], TargetText = doc.Text[caret..te], RightContext = doc.Text[te..], RightContextEndUtf16Offset = doc.Text.Length,
            RightContextTruncated = false, EndOfLine = "LF", Indentation = "", QualityFlags = [], Split = "train", SplitGroup = "x",
            SemanticStatus = SemanticStatus.NotAttempted, SemanticReason = null, ConfigVersion = "test", ConfigSha256 = new string('0', 64),
        };
    }

    /// <summary>Offset just after the first occurrence of <paramref name="marker"/> (or at its start when <paramref name="atStart"/>).</summary>
    public static int After(string text, string marker, bool atStart = false)
    {
        var i = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(i >= 0, $"marker not found: {marker}");
        return atStart ? i : i + marker.Length;
    }
}
