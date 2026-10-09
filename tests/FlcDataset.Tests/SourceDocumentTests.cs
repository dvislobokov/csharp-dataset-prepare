using System.Text;
using FlcDataset.Core;
using Microsoft.CodeAnalysis.Text;

namespace FlcDataset.Tests;

public class SourceDocumentTests
{
    [Theory]
    [InlineData("a\nb\r\nc\rd")]
    [InlineData("no newline at eof")]
    [InlineData("ends with newline\n")]
    [InlineData("crlf\r\n\r\n")]
    [InlineData("u2028\u2028u2029\u2029nel\u0085end")]
    [InlineData("")]
    [InlineData("\r\n")]
    [InlineData("tab\tindent\n\t\tx")]
    public void LinesMatchRoslyn(string text)
    {
        var doc = SourceDocument.FromText(text);
        var roslyn = SourceText.From(text).Lines;
        Assert.Equal(roslyn.Count, doc.Lines.Count);
        for (int i = 0; i < roslyn.Count; i++)
        {
            Assert.Equal(roslyn[i].Start, doc.Lines[i].Start);
            Assert.Equal(roslyn[i].End, doc.Lines[i].End);
            Assert.Equal(roslyn[i].EndIncludingLineBreak, doc.Lines[i].EndIncludingBreak);
        }
    }

    [Fact]
    public void Utf16OffsetLineColumnRoundTrip_WithSurrogatesTabsAndBom()
    {
        var text = "\tvar s = \"😀\";\r\n\tvar café = 1; // 😀😀\nint αβ = 2;";
        var doc = SourceDocument.FromText(text, hasBom: true);
        for (int off = 0; off <= text.Length; off++)
        {
            var (line, col) = doc.LineColumn(off);
            Assert.Equal(off, doc.OffsetOf(line, col));
            var expectedBytes = 3 + Encoding.UTF8.GetByteCount(text.AsSpan(0, off));
            if (off == 0 || off == text.Length || !char.IsLowSurrogate(text[off]) || !char.IsHighSurrogate(text[off - 1]))
                Assert.Equal(expectedBytes, doc.ByteOffset(off));
        }
        Assert.True(doc.HasBom);
        Assert.Equal(Hashing.Sha256Hex(SourceDocument.ToBytes(text, true)), doc.Sha256);
    }

    [Fact]
    public void DecodePreservesBomAndBytes()
    {
        var bytes = File.ReadAllBytes(Path.Combine(TestUtil.FixtureRepo, "src/Shop/Naming.cs"));
        var doc = SourceDocument.TryDecode(bytes, out var err)!;
        Assert.Null(err);
        Assert.True(doc.HasBom);
        Assert.Equal("crlf", doc.NewlineStyle());
        Assert.Equal(bytes, SourceDocument.ToBytes(doc.Text, doc.HasBom));
        Assert.Equal(EndOfLine.None, doc.Lines[^1].Break); // file has no trailing newline
    }

    [Fact]
    public void RejectsInvalidUtf8AndUtf16()
    {
        Assert.Null(SourceDocument.TryDecode([0x61, 0xC3, 0x28], out var e1));
        Assert.Equal("invalid_utf8", e1);
        Assert.Null(SourceDocument.TryDecode([0xFF, 0xFE, 0x61, 0x00], out var e2));
        Assert.Equal("utf16_encoding", e2);
    }

    [Fact]
    public void StableIdAndUniformAreDeterministic()
    {
        Assert.Equal(Hashing.StableId("a", "b"), Hashing.StableId("a", "b"));
        Assert.NotEqual(Hashing.StableId("a", "b"), Hashing.StableId("ab", ""));
        var u = Hashing.Uniform("seed", "x");
        Assert.InRange(u, 0, 1);
        Assert.Equal(u, Hashing.Uniform("seed", "x"));
    }

    [Theory]
    [InlineData("**/obj/**", "src/A/obj/x.cs", true)]
    [InlineData("**/obj/**", "obj/x.cs", true)]
    [InlineData("**/*.g.cs", "a/b/c.g.cs", true)]
    [InlineData("src/**/*.cs", "src/a.cs", true)]
    [InlineData("src/**/*.cs", "tests/a.cs", false)]
    [InlineData("**/Migrations/**", "src/X/Migrations/2024_Init.cs", true)]
    [InlineData("*.cs", "a/b.cs", false)]
    public void GlobMatches(string pattern, string path, bool expected) => Assert.Equal(expected, new Glob(pattern).IsMatch(path));
}

public class HashingTests
{
    [Fact]
    public void CaretUniformMatchesGenericUniform()
    {
        foreach (var off in new[] { 0, 1, 42, 123456, int.MaxValue })
            Assert.Equal(Hashing.Uniform("20261009", "caret", new string('a', 64), off.ToString()), Hashing.CaretUniform("20261009", new string('a', 64), off));
    }
}

public class PromptRendererTests
{
    static FlcSampleRecord Sample(string left, string target) => TestUtil.SampleAt(SourceDocument.FromText(left + target + "\n}"), "src/A.cs", left.Length);

    [Fact]
    public void PromptEndsAtCaretAndCompletionEndsWithEol()
    {
        var s = Sample("class A\n{\n    int M(int id) => Get", "ById(id);");
        var t = PromptRenderer.Render(s, null, new PromptOptions());
        Assert.EndsWith("int M(int id) => Get<|complete|>", t.Prompt);
        Assert.Equal("ById(id);<|eol|>", t.Completion);
        Assert.DoesNotContain("ById", t.Prompt);
        Assert.DoesNotContain("<|sem|>", t.Prompt);
        Assert.StartsWith("<|cs|><|path|>src/A.cs\n<|code|>\n", t.Prompt);
    }

    [Fact]
    public void SemanticBlockRespectsBudgetAndPriority()
    {
        var s = Sample("class A { void M() { repo.", "Get();");
        var sem = new SemanticRecord
        {
            SampleId = s.SampleId, VisibilityPolicy = VisibilityPolicy.EditorSnapshot, Status = SemanticStatus.Resolved,
            ReturnType = "void", ExpectedType = "Order", ReceiverType = "IRepo", ReceiverKind = "instance",
            Members = Enumerable.Range(0, 100).Select(i => new SymbolFact { Name = "M" + i, Kind = "method", Signature = $"Task<Order?> M{i}(Guid id)" }).ToList(),
        };
        var t = PromptRenderer.Render(s, sem, new PromptOptions { MaxSemanticChars = 200 });
        var block = t.Prompt[(t.Prompt.IndexOf("<|sem|>\n") + 8)..t.Prompt.IndexOf("<|code|>")];
        Assert.True(block.Length <= 200, block);
        Assert.Contains("EXPECT Order\n", block);
        Assert.Contains("RECV IRepo\n", block);
        Assert.Contains("MEMBER M0(Guid id)->Task<Order?>", block);
        Assert.True(t.SemanticItemsDropped > 0);
        // canonical order: RET before EXPECT before RECV before MEMBER
        Assert.True(block.IndexOf("RET") < block.IndexOf("EXPECT") && block.IndexOf("EXPECT") < block.IndexOf("RECV") && block.IndexOf("RECV") < block.IndexOf("MEMBER"));
    }

    [Fact]
    public void CodeWindowIsCutOnLineBoundaryFromTheLeft()
    {
        var left = string.Concat(Enumerable.Range(0, 200).Select(i => $"// line {i}\n")) + "var x = ";
        var t = PromptRenderer.Render(Sample(left, "1;"), null, new PromptOptions { MaxCodeChars = 100 });
        var code = t.Prompt[(t.Prompt.IndexOf("<|code|>\n") + 9)..^"<|complete|>".Length];
        Assert.True(code.Length <= 100);
        Assert.StartsWith("// line", code);
        Assert.EndsWith("var x = ", code);
        Assert.True(t.CodeTruncated);
    }

    [Theory]
    [InlineData("Task<Order?> GetByIdAsync(Guid id)", "GetByIdAsync(Guid id)->Task<Order?>")]
    [InlineData("Results<Ok<T>, NotFound> Get<T>(int a)", "Get<T>(int a)->Results<Ok<T>, NotFound>")]
    [InlineData("void Run()", "Run()->void")]
    public void CompactSignature(string input, string expected) => Assert.Equal(expected, PromptRenderer.CompactSig(input));
}

public class TypeBlockRenderTests
{
    [Fact]
    public void TypeLinesRenderAndCanBeDisabled()
    {
        var s = TestUtil.SampleAt(SourceDocument.FromText("class A { void M(Customer c) { var x = \n}"), "src/A.cs", 39);
        var sem = new SemanticRecord
        {
            SampleId = s.SampleId, VisibilityPolicy = VisibilityPolicy.EditorSnapshot, Status = SemanticStatus.Resolved,
            ContextTypes = [new TypeContract { Name = "Customer", Kind = "class", Source = "parameter", Members =
                [new SymbolFact { Name = "new", Kind = "constructor", Signature = "new(string name)" }, new SymbolFact { Name = "Name", Kind = "property", Type = "string" },
                 new SymbolFact { Name = "Rename", Kind = "method", Signature = "void Rename(string name)" }] }],
        };
        var v2 = PromptRenderer.Render(s, sem, new PromptOptions());
        Assert.Contains("TYPE Customer: new(string name); Name:string; Rename(string name)->void\n", v2.Prompt);
        Assert.Equal("flc-prompt/v2", v2.PromptFormat);
        var v1 = PromptRenderer.Render(s, sem, new PromptOptions { IncludeTypes = false, Format = PromptRenderer.FormatV1 });
        Assert.DoesNotContain("TYPE ", v1.Prompt);
    }
}
