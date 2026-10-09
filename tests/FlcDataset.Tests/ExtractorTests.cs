using FlcDataset.Cli;
using FlcDataset.Core;
using FlcDataset.Extraction;

namespace FlcDataset.Tests;

public class ExtractorTests
{
    const string Code = "namespace N;\n\npublic class C\n{\n    public int M(int a, string b)\n    {\n        var x = a + 1;\n        return Helper(x, b.Length);\n    }\n\n    int Helper(int p, int q) => p * q;\n}\n";

    static void AssertInvariants(SourceDocument doc, FlcSampleRecord s)
    {
        var t = doc.Text;
        Assert.Equal(t, string.Concat(t.AsSpan(0, s.CaretUtf16Offset), s.TargetText, t.AsSpan(s.TargetEndUtf16Offset)));
        Assert.Equal(s.TargetText, t[s.CaretUtf16Offset..s.TargetEndUtf16Offset]);
        Assert.DoesNotContain(s.TargetText, SourceDocument.IsLineBreakChar);
        Assert.NotEmpty(s.TargetText);
        Assert.False(char.IsWhiteSpace(s.TargetText[^1]));
        Assert.Equal(t[s.LeftContextStartUtf16Offset..s.CaretUtf16Offset], s.LeftContext);
        Assert.Equal(t[s.TargetEndUtf16Offset..s.RightContextEndUtf16Offset], s.RightContext);
        var line = doc.Lines[s.CaretLineZeroBased];
        Assert.Equal(line.Start, s.LineStartUtf16Offset);
        Assert.Equal(s.CaretUtf16Offset - line.Start, s.CaretColumnUtf16ZeroBased);
        Assert.True(t[s.TargetEndUtf16Offset..line.End].All(char.IsWhiteSpace));
        Assert.Equal(doc.ByteOffset(s.CaretUtf16Offset), s.CaretByteOffset);
        Assert.Equal(doc.ByteOffset(s.TargetEndUtf16Offset), s.TargetEndByteOffset);
        if (s.CaretUtf16Offset > 0 && s.CaretUtf16Offset < t.Length)
            Assert.False(char.IsLowSurrogate(t[s.CaretUtf16Offset]) && char.IsHighSurrogate(t[s.CaretUtf16Offset - 1]));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void ReconstructionHoldsForEverySample(string newline)
    {
        var text = Code.Replace("\n", newline);
        var doc = SourceDocument.FromText(text);
        var r = new CaretExtractor(TestUtil.AllCarets()).Extract(doc, TestUtil.Ctx());
        Assert.NotEmpty(r.Samples);
        foreach (var s in r.Samples)
        {
            AssertInvariants(doc, s);
            if (s.CaretLineZeroBased < doc.Lines.Count - 1) Assert.Equal(newline == "\n" ? "LF" : "CRLF", s.EndOfLine);
            Assert.StartsWith(newline == "\n" ? "\n" : "\r\n", s.RightContext[s.RightContext.TakeWhile(c => c == ' ' || c == '\t').Count()..]);
        }
    }

    [Fact]
    public void UnicodeBomCrlfEofFixtureInvariants()
    {
        var bytes = File.ReadAllBytes(Path.Combine(TestUtil.FixtureRepo, "src/Shop/Naming.cs"));
        var doc = SourceDocument.TryDecode(bytes, out _)!;
        var r = new CaretExtractor(TestUtil.AllCarets()).Extract(doc, TestUtil.Ctx("src/Shop/Naming.cs"));
        Assert.NotEmpty(r.Samples);
        foreach (var s in r.Samples) AssertInvariants(doc, s);
        Assert.Contains(r.Samples, s => s.EndOfLine == "EOF");
        Assert.Contains(r.Samples, s => s.TargetText.Contains("😀") || s.QualityFlags.Contains("non_ascii_target"));
        Assert.Contains(r.Samples, s => s.Indentation == "\t");
        // Byte offsets include the 3-byte BOM.
        Assert.All(r.Samples, s => Assert.True(s.CaretByteOffset >= 3));
    }

    [Fact]
    public void TruncatedLeftContextStillReconstructsFromFullSource()
    {
        var cfg = TestUtil.AllCarets() with { Context = new ContextConfig { LeftChars = 10, RightChars = 5 } };
        var doc = SourceDocument.FromText(Code);
        var r = new CaretExtractor(cfg).Extract(doc, TestUtil.Ctx());
        var s = r.Samples.First(x => x.CaretUtf16Offset > 20);
        Assert.True(s.LeftContextTruncated);
        Assert.Equal(10, s.LeftContext.Length);
        AssertInvariants(doc, s);
        Assert.True(r.Samples.Any(x => x.RightContextTruncated));
    }

    [Fact]
    public void LeftContextNeverSplitsSurrogatePair()
    {
        var text = "class C { string s = \"😀😀😀😀😀😀\"; int x = 1; }\n";
        var cfg = TestUtil.AllCarets();
        for (int left = 1; left < 30; left++)
        {
            var r = new CaretExtractor(cfg with { Context = new ContextConfig { LeftChars = left, RightChars = left } }).Extract(SourceDocument.FromText(text), TestUtil.Ctx());
            foreach (var s in r.Samples)
            {
                Assert.False(s.LeftContext.Length > 0 && char.IsLowSurrogate(s.LeftContext[0]));
                Assert.False(s.RightContext.Length > 0 && char.IsHighSurrogate(s.RightContext[^1]));
            }
        }
    }

    [Fact]
    public void CaretKindsAreDetected()
    {
        var r = TestUtil.Extract(File.ReadAllText(Path.Combine(TestUtil.FixtureRepo, "src/Shop/OrderService.cs")));
        var kinds = r.Samples.Select(s => s.CaretKind).ToHashSet();
        foreach (var k in new[] { "line_start", "identifier_partial", "member_access", "argument_list", "after_keyword", "control_flow", "lambda_body", "linq", "log_message" })
            Assert.Contains(k, kinds);
        var log = r.Samples.Where(s => s.CaretKind == "log_message").ToList();
        Assert.Contains(log, s => s.TargetText.StartsWith("Order {OrderId} was not found\""));
        Assert.Contains(log, s => s.TargetText.StartsWith("\"Order {OrderId}"));
        var member = r.Samples.First(s => s.CaretKind == "member_access" && s.LeftContext.EndsWith("repository."));
        Assert.Equal("GetByIdAsync(id, ct);", member.TargetText);
        var ret = r.Samples.First(s => s.CaretKind == "after_keyword" && s.CaretSubkind == "return" && s.TargetText == "order;");
        Assert.NotNull(ret);
        var start = r.Samples.First(s => s.CaretKind == "line_start" && s.TargetText.StartsWith("int count"));
        Assert.Equal("local_declaration", start.CaretSubkind);
    }

    [Fact]
    public void NegativeStrataAreCountedNotSilentlyDropped()
    {
        var r = TestUtil.Extract(File.ReadAllText(Path.Combine(TestUtil.FixtureRepo, "src/Shop/Strings.cs")));
        Assert.True(r.Counts.GetValueOrDefault("lines.inactive_preprocessor_code") >= 1);
        Assert.True(r.Counts.GetValueOrDefault("lines.preprocessor_directive") >= 2);
        Assert.True(r.Counts.GetValueOrDefault("lines.inside_raw_string") >= 1);
        Assert.True(r.Counts.GetValueOrDefault("excluded.in_string") >= 1);
        Assert.True(r.Counts.GetValueOrDefault("excluded.in_interpolated_string") >= 1);
        Assert.True(r.Counts.GetValueOrDefault("excluded.in_comment") >= 1);
        Assert.True(r.Counts.GetValueOrDefault("excluded.raw_multiline_string") >= 1);
        Assert.NotEmpty(r.Exclusions);
        // No sample may sit inside disabled code or a string literal.
        Assert.DoesNotContain(r.Samples, s => s.TargetText.Contains("Hidden"));
        Assert.DoesNotContain(r.Samples, s => s.LeftContext.EndsWith("\"hello"));
        Assert.Contains(r.Samples, s => s.QualityFlags.Contains("target_has_comment"));
    }

    [Fact]
    public void SamplingIsDeterministicAndRespectsWeightsAndCaps()
    {
        var text = File.ReadAllText(Path.Combine(TestUtil.FixtureRepo, "src/Shop/OrderService.cs"));
        var cfg = TestUtil.AllCarets() with
        {
            Sampling = new SamplingConfig { Weights = new() { ["line_start"] = 0.5, ["member_access"] = 1 }, MaxSamplesPerLine = 1, MaxSamplesPerFile = 7 },
        };
        var a = TestUtil.Extract(text, cfg).Samples.Select(s => FlcJson.Serialize(s)).ToList();
        var b = TestUtil.Extract(text, cfg).Samples.Select(s => FlcJson.Serialize(s)).ToList();
        Assert.Equal(a, b);
        var samples = TestUtil.Extract(text, cfg).Samples;
        Assert.True(samples.Count <= 7);
        Assert.All(samples, s => Assert.Contains(s.CaretKind, new[] { "line_start", "member_access" }));
        Assert.Equal(samples.Count, samples.Select(s => s.CaretLineZeroBased).Distinct().Count());
        Assert.Equal(samples.OrderBy(s => s.CaretUtf16Offset).Select(s => s.SampleId), samples.Select(s => s.SampleId));
        // Different seed -> different (but still valid) selection.
        var c = TestUtil.Extract(text, cfg with { Seed = 999 }).Samples.Select(s => s.SampleId).ToList();
        Assert.NotEqual(samples.Select(s => s.SampleId).ToList(), c);
    }

    [Fact]
    public void TrivialTargetsAreFlaggedAndDownWeighted()
    {
        var text = "class C\n{\n    void M()\n    {\n        F();\n    }\n}\n";
        var all = TestUtil.Extract(text).Samples;
        Assert.Contains(all, s => s.TargetText == "}" && s.QualityFlags.Contains("trivial_target"));
        var cfg = TestUtil.AllCarets() with { Sampling = TestUtil.AllCarets().Sampling with { TrivialTargetKeepProbability = 0 } };
        Assert.DoesNotContain(TestUtil.Extract(text, cfg).Samples, s => s.QualityFlags.Contains("trivial_target"));
    }

    [Fact]
    public void LargeSyntheticFileIsBoundedByCaps()
    {
        var sb = new System.Text.StringBuilder("class Big\n{\n    void M()\n    {\n");
        for (int i = 0; i < 20000; i++) sb.Append("        var v").Append(i).Append(" = Compute(").Append(i).Append(").ToString();\n");
        sb.Append("    }\n}\n");
        var cfg = TestUtil.AllCarets() with { Sampling = TestUtil.AllCarets().Sampling with { MaxSamplesPerFile = 500, MaxSamplesPerLine = 2 } };
        long before = GC.GetTotalAllocatedBytes(true);
        var r = TestUtil.Extract(sb.ToString(), cfg);
        long allocated = GC.GetTotalAllocatedBytes(true) - before;
        Assert.Equal(500, r.Samples.Count);
        Assert.True(r.Counts["candidates.total"] > 100000);
        // Output is capped even though candidates are many; allocation stays proportional to file size, not candidates^2.
        Assert.True(allocated < 2_000_000_000, $"allocated {allocated}");
    }

    [Fact]
    public void SecretLinesAreExcluded()
    {
        var text = "class C\n{\n    string k = \"AKIAABCDEFGHIJKLMNOP\";\n    int ok = 1;\n}\n";
        var r = TestUtil.Extract(text);
        Assert.DoesNotContain(r.Samples, s => s.TargetText.Contains("AKIA"));
        Assert.True(r.Counts.GetValueOrDefault("excluded.secret_detected") > 0);
    }
}

public class SecretRuleTests
{
    [Fact]
    public void IdentifierAssignmentsAreNotSecretsButLiteralsAre()
    {
        var cfg = TestUtil.AllCarets();
        var text = "class C\n{\n    void M(string password)\n    {\n        var s = new Settings { Password = password };\n        var k = \"AKIAABCDEFGHIJKLMNOP\";\n        var dbPassword = \"Pass@word1234\";\n        var pw = \"x\";\n    }\n}\n";
        var r = TestUtil.Extract(text.Replace("var k = \"AKIAABCDEFGHIJKLMNOP\";\n", ""), cfg);
        Assert.Contains(r.Samples, s => s.TargetText.Contains("Password = password"));
        Assert.DoesNotContain(r.Samples, s => s.TargetText.Contains("Pass@word1234"));
        var withKey = TestUtil.Extract(text, cfg);
        Assert.DoesNotContain(withKey.Samples, s => s.TargetText.Contains("AKIA"));
    }
}

public class UnicodeWhitespaceTests
{
    [Fact]
    public async Task NonBreakingSpaceIndentationPassesSchemaAndReconstruction()
    {
        var dir = TestUtil.TempDir();
        File.WriteAllText(Path.Combine(dir, "LICENSE"), "Permission is hereby granted, free of charge...\nTHE SOFTWARE IS PROVIDED \"AS IS\"");
        File.WriteAllText(Path.Combine(dir, "A.cs"), "class A\n{\n    void M(int x)\n    {\n　　return;\n    }\n}\n");
        var cfg = TestUtil.AllCarets() with { RepositoryId = "fixture/nbsp", Discovery = new DiscoveryConfig { Include = ["**/*.cs"] } };
        var outDir = TestUtil.TempDir();
        await ExtractionPipeline.RunAsync(new PipelineOptions { RepoPath = dir, OutputDir = outDir, Config = cfg }, TestContext.Current.CancellationToken);
        DatasetValidator.SchemaDirectory = Path.Combine(AppContext.BaseDirectory, "schemas");
        var report = new DatasetValidator().Validate(outDir, dir);
        Assert.True(report.Ok, string.Join("\n", report.FailureExamples));
        Assert.Contains(Jsonl.Read<FlcSampleRecord>(Path.Combine(outDir, "samples.jsonl")), s => s.Indentation == "    ");
    }
}
