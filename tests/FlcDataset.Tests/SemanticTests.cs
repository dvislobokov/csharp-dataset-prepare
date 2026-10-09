using FlcDataset.Cli;
using FlcDataset.Core;
using FlcDataset.Extraction;
using FlcDataset.Semantics;

namespace FlcDataset.Tests;

/// <summary>Semantic facts on the safe adhoc workspace built from the fixture repository (no MSBuild, no restore).</summary>
public class SemanticTests : IDisposable
{
    readonly AdhocDocumentSource _source;
    readonly SourceDocument _service;
    const string ServicePath = "src/Shop/OrderService.cs";
    static readonly SemanticConfig Cfg = new();

    public SemanticTests()
    {
        var cfg = TestUtil.AllCarets();
        var files = new Discoverer(cfg, RepositoryInfo.Inspect(TestUtil.FixtureRepo, cfg)).Discover()
            .Where(f => f.Document is not null).Select(f => (f.Record.RelativePath, f.Record.Project, f.Document!.Text)).ToList();
        _source = AdhocDocumentSource.Create(TestUtil.FixtureRepo, files);
        _service = SourceDocument.FromText(File.ReadAllText(Path.Combine(TestUtil.FixtureRepo, ServicePath)));
    }

    public void Dispose() => _source.Dispose();

    async Task<SemanticRecord> At(int caret, string policy = VisibilityPolicy.EditorSnapshot, string kind = "line_start")
    {
        var doc = _source.Find(ServicePath, out var reason);
        Assert.NotNull(doc);
        return await SemanticAnalyzer.AnalyzeAsync(doc!, TestUtil.SampleAt(_service, ServicePath, caret, kind), policy, Cfg, default);
    }

    static string[] Names(IEnumerable<SymbolFact> f) => f.Select(x => x.Name).ToArray();

    [Fact]
    public async Task LocalDeclaredInsideTargetIsNotInScope()
    {
        // Caret at start of "var expensive = ...": the target declares `expensive`; it must not leak.
        var caret = TestUtil.After(_service.Text, "var expensive", atStart: true);
        foreach (var policy in new[] { VisibilityPolicy.EditorSnapshot, VisibilityPolicy.StrictPrefix })
        {
            var r = await At(caret, policy);
            Assert.Contains("order", Names(r.Locals));
            Assert.DoesNotContain("expensive", Names(r.Locals));
            Assert.DoesNotContain("count", Names(r.Locals)); // declared on a later line: in C# scope but not yet usable
            Assert.Empty(r.Leakage!.Violations);
            Assert.Contains("id", Names(r.Parameters));
            Assert.Contains("ct", Names(r.Parameters));
            Assert.Contains(r.Parameters, p => p.Name == "repository" && p.Kind == "primary_ctor_parameter");
        }
    }

    [Fact]
    public async Task RankingWindowEndsAtLineStartSoThePartlyTypedNameDoesNotCount()
    {
        // `GetByIdAsync` first occurs on the caret line; with the caret inside it, the old window [pos - 20000, pos)
        // counted the original identifier (completed by the hidden target) as "already used in the prefix".
        var caret = TestUtil.After(_service.Text, "repository.GetBy");
        var sample = TestUtil.SampleAt(_service, ServicePath, caret, "identifier_partial");
        var ctx = await FileSemanticContext.CreateAsync(_source.Find(ServicePath, out _)!, default);
        Assert.True(ctx.OccursIn("GetByIdAsync", Math.Max(0, caret - 20000), caret)); // the leak the window must avoid
        var (start, end) = SemanticAnalyzer.RankingWindow(sample);
        Assert.Equal(sample.LineStartUtf16Offset, end);
        Assert.False(ctx.OccursIn("GetByIdAsync", start, end));
        Assert.True(ctx.OccursIn("repository", start, end)); // the primary constructor parameter is genuinely in the prefix
    }

    [Theory]
    [InlineData("repository.GetBy", "IOrderRepository", "GetByIdAsync")]
    [InlineData("order?.Cust", "Order", "Customer")]
    public async Task PartlyTypedMemberNameStillReportsReceiverAndMembers(string marker, string receiver, string member)
    {
        var caret = TestUtil.After(_service.Text, marker);
        var r = await At(caret, kind: "identifier_partial");
        Assert.Equal(receiver, r.ReceiverType);
        Assert.Equal("instance", r.ReceiverKind);
        Assert.Contains(member, Names(r.Members)); // legitimate: an accessible member of the typed receiver
        Assert.Empty(r.Leakage!.Violations);
    }

    [Fact]
    public void DuplicateProjectReferenceDoesNotBreakTheWorkspace()
    {
        var dir = Directory.CreateTempSubdirectory("flc-dupref").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "A"));
            Directory.CreateDirectory(Path.Combine(dir, "B"));
            File.WriteAllText(Path.Combine(dir, "B/B.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
            File.WriteAllText(Path.Combine(dir, "A/A.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup Condition="'$(X)' == ''"><ProjectReference Include="../B/B.csproj" /></ItemGroup>
                  <ItemGroup Condition="'$(X)' != ''"><ProjectReference Include="..\B\B.csproj" /></ItemGroup>
                </Project>
                """);
            using var src = AdhocDocumentSource.Create(dir, [("A/C.cs", "A/A.csproj", "class C { B.D d; }"), ("B/D.cs", "B/B.csproj", "namespace B; public class D {}")]);
            Assert.NotNull(src.Find("A/C.cs", out _));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task VarTypeIsResolvedFromInference()
    {
        var caret = TestUtil.After(_service.Text, "int count", atStart: true);
        var r = await At(caret);
        Assert.Equal("Order?", r.Locals.Single(l => l.Name == "order").Type);
        Assert.Equal("List<string>", r.Locals.Single(l => l.Name == "expensive").Type);
        Assert.Equal("annotated", r.Locals.Single(l => l.Name == "order").NullableAnnotation);
        Assert.Equal("Task<Order>", r.ReturnType);
        Assert.Contains("GetOrderAsync", r.EnclosingSymbol);
    }

    [Fact]
    public async Task MemberAccessListsAccessibleMembersWithOverloads()
    {
        var caret = TestUtil.After(_service.Text, "await repository.");
        var r = await At(caret, kind: "member_access");
        Assert.Equal("IOrderRepository", r.ReceiverType);
        Assert.Equal("instance", r.ReceiverKind);
        var get = r.Members.Single(m => m.Name == "GetByIdAsync");
        Assert.Equal(2, get.Overloads);
        Assert.Contains("AddAsync", Names(r.Members));
        Assert.DoesNotContain("order", Names(r.Locals)); // `var order = await repository.` — declarator still being typed
        Assert.Equal(SemanticStatus.Resolved, r.Status);
        Assert.Contains("GetByIdAsync", r.Leakage!.CoveredTargetIdentifiers); // legitimate: member exists independently
        Assert.Empty(r.Leakage.Violations);
        var strict = await At(caret, VisibilityPolicy.StrictPrefix, "member_access");
        Assert.Equal("IOrderRepository", strict.ReceiverType);
    }

    [Fact]
    public async Task InvocationCandidatesAndExpectedArgumentType()
    {
        var caret = TestUtil.After(_service.Text, "repository.GetByIdAsync(");
        var r = await At(caret, kind: "argument_list");
        Assert.Equal(2, r.InvocationCandidates.Count);
        Assert.All(r.InvocationCandidates, c => Assert.Equal("Guid", c.ParameterType));
        Assert.Equal("Guid", r.ExpectedType);
        Assert.Equal("argument", r.ExpectedTypeSource);
    }

    [Fact]
    public async Task ExpectedTypeAfterReturnUnwrapsTask()
    {
        var caret = TestUtil.After(_service.Text, "return order");
        caret -= "order".Length;
        var r = await At(caret, kind: "after_keyword");
        Assert.Equal("Order", r.ExpectedType);
        Assert.Equal("return", r.ExpectedTypeSource);
    }

    [Fact]
    public async Task MemberUsedOnlyInRemovedSuffixIsNotInPayload()
    {
        // `total += line.Price * line.Quantity;` — Price/Quantity appear only in the hidden target in this file.
        var caret = TestUtil.After(_service.Text, "total += line", atStart: true);
        var r = await At(caret);
        // Not copied into scope facts from the hidden line...
        var withoutTypes = SemanticAnalyzer.RenderPrompt(r with { ContextTypes = [] });
        Assert.DoesNotContain("Quantity", withoutTypes);
        Assert.DoesNotContain("Price", withoutTypes);
        // ...but legitimately listed as members of the local's type OrderLine, which exist independently of the target.
        Assert.Contains(r.ContextTypes, t => t.Name == "OrderLine" && t.Source == "local" && t.Members.Any(m => m.Name == "Quantity"));
        Assert.Contains(r.Locals, l => l.Name == "line" && l.Type == "OrderLine");
        Assert.Contains(r.Locals, l => l.Name == "total");
        Assert.Empty(r.Leakage!.Violations);
    }

    [Fact]
    public async Task StaticContextExcludesInstanceMembersAndPrimaryCtorParameters()
    {
        var caret = TestUtil.After(_service.Text, "=> order?.Customer", atStart: true) + 3;
        var r = await At(caret, kind: "lambda_body");
        Assert.DoesNotContain("_maxLines", Names(r.ThisMembers));
        Assert.DoesNotContain("GetOrderAsync", Names(r.ThisMembers));
        Assert.Contains("Describe", Names(r.ThisMembers));
        Assert.DoesNotContain(r.Parameters, p => p.Kind == "primary_ctor_parameter");
        Assert.Equal("string", r.ExpectedType);
    }

    [Fact]
    public async Task ThisMembersAreReported()
    {
        var caret = TestUtil.After(_service.Text, "int count", atStart: true);
        var r = await At(caret);
        Assert.Contains(r.ThisMembers, m => m.Name == "_maxLines" && m.Kind == "field" && m.Type == "int");
        Assert.Contains("Sum", Names(r.ThisMembers));
        Assert.Contains("THIS ", r.Prompt);
    }

    [Fact]
    public async Task EnricherFallsBackWithReasonsAndNeverCrashes()
    {
        var cfg = TestUtil.AllCarets() with { Semantic = new SemanticConfig { SubsetFraction = 1.0 } };
        var counters = new Counters();
        var lat = new LatencyRecorder();
        var samples = new CaretExtractor(cfg).Extract(_service, TestUtil.Ctx(ServicePath)).Samples.Take(5).ToList();

        // Document missing from the workspace -> syntax_fallback with reason, sample kept.
        var missing = new SemanticEnricher(_source, cfg, "semantic_best_effort");
        var (kept, recs) = await missing.EnrichAsync(TestUtil.Ctx("src/Nope.cs"), _service, samples, counters, lat, default);
        Assert.Equal(samples.Count, kept.Count);
        Assert.All(kept, s => Assert.Equal(SemanticStatus.SyntaxFallback, s.SemanticStatus));
        Assert.All(kept, s => Assert.Equal("document_not_in_workspace", s.SemanticReason));
        Assert.Empty(recs);

        // semantic_required drops instead (and counts it).
        var required = new SemanticEnricher(_source, cfg, "semantic_required");
        var (dropped, _) = await required.EnrichAsync(TestUtil.Ctx("src/Nope.cs"), _service, samples, counters, lat, default);
        Assert.Empty(dropped);
        Assert.Equal(samples.Count, counters.Get("semantic.required_dropped"));

        // Timeout -> failed with reason code; syntax sample still kept in best-effort.
        var slow = new SemanticEnricher(_source, cfg with { Semantic = cfg.Semantic with { TimeoutMs = 0 } }, "semantic_best_effort");
        var (timed, trecs) = await slow.EnrichAsync(TestUtil.Ctx(ServicePath), _service, samples, counters, lat, default);
        Assert.Equal(samples.Count, timed.Count);
        Assert.All(trecs, r => Assert.Equal("timeout", r.Reason));
    }

    [Fact]
    public async Task MissingReferencesDegradeToPartialResolution()
    {
        var cfg = TestUtil.AllCarets();
        var files = new Discoverer(cfg, RepositoryInfo.Inspect(TestUtil.FixtureRepo, cfg)).Discover()
            .Where(f => f.Document is not null).Select(f => (f.Record.RelativePath, f.Record.Project, f.Document!.Text)).ToList();
        using var noRefs = AdhocDocumentSource.Create(TestUtil.FixtureRepo, files, referenceDirs: []);
        var doc = noRefs.Find(ServicePath, out _)!;
        var caret = TestUtil.After(_service.Text, "order.Lines.Where(l => l.");
        var r = await SemanticAnalyzer.AnalyzeAsync(doc, TestUtil.SampleAt(_service, ServicePath, caret, "member_access"), VisibilityPolicy.EditorSnapshot, Cfg, default);
        Assert.NotEqual(SemanticStatus.Failed, r.Status);
        Assert.Empty(r.Leakage!.Violations);
    }

    [Fact]
    public async Task MalformedSourceIsAnalyzedWithoutCrash()
    {
        var bad = "class Broken { void M() { var x = ; if ( { } \n int y = x.\n }";
        var doc = SourceDocument.FromText(bad);
        var files = new List<(string, string?, string)> { ("src/Broken.cs", null, bad) };
        using var src = AdhocDocumentSource.Create(TestUtil.FixtureRepo, files);
        var d = src.Find("src/Broken.cs", out _)!;
        foreach (var s in new CaretExtractor(TestUtil.AllCarets()).Extract(doc, TestUtil.Ctx("src/Broken.cs")).Samples)
        {
            var r = await SemanticAnalyzer.AnalyzeAsync(d, s, VisibilityPolicy.EditorSnapshot, Cfg, default);
            Assert.NotNull(r.Status);
            Assert.Empty(r.Leakage!.Violations);
        }
    }

    [Fact]
    public async Task FullFixtureEnrichmentHasNoLeakViolations()
    {
        var cfg = TestUtil.AllCarets() with { Semantic = new SemanticConfig { SubsetFraction = 1.0 } };
        var samples = new CaretExtractor(cfg).Extract(_service, TestUtil.Ctx(ServicePath)).Samples;
        var counters = new Counters();
        var (outSamples, recs) = await new SemanticEnricher(_source, cfg, "semantic_best_effort")
            .EnrichAsync(TestUtil.Ctx(ServicePath), _service, samples, counters, new LatencyRecorder(), default);
        Assert.Equal(samples.Count * 2, recs.Count);
        Assert.All(recs, r => Assert.Empty(r.Leakage?.Violations ?? []));
        Assert.DoesNotContain(outSamples, s => s.SemanticStatus == SemanticStatus.NotAttempted);
        Assert.True(counters.Get("semantic.status.resolved") > samples.Count / 2);
    }
}

public class SemanticArtifactTests : IDisposable
{
    readonly AdhocDocumentSource _source;
    readonly SourceDocument _service;
    const string ServicePath = "src/Shop/OrderService.cs";

    public SemanticArtifactTests()
    {
        var cfg = TestUtil.AllCarets();
        var files = new Discoverer(cfg, RepositoryInfo.Inspect(TestUtil.FixtureRepo, cfg)).Discover()
            .Where(f => f.Document is not null).Select(f => (f.Record.RelativePath, f.Record.Project, f.Document!.Text)).ToList();
        _source = AdhocDocumentSource.Create(TestUtil.FixtureRepo, files);
        _service = SourceDocument.FromText(File.ReadAllText(Path.Combine(TestUtil.FixtureRepo, ServicePath)));
    }

    public void Dispose() => _source.Dispose();

    [Fact]
    public async Task MethodBodyReparsedAsFieldsIsNotReportedAsMembers()
    {
        // Removing the header line "public decimal Sum(Order order)" makes the body parse as class-level declarations
        // (e.g. a field `decimal total`). Those symbols come from text after the caret and must be dropped.
        var caret = TestUtil.After(_service.Text, "public decimal Sum", atStart: true);
        var r = await SemanticAnalyzer.AnalyzeAsync(_source.Find(ServicePath, out _)!, TestUtil.SampleAt(_service, ServicePath, caret),
            VisibilityPolicy.EditorSnapshot, new SemanticConfig(), TestContext.Current.CancellationToken);
        Assert.DoesNotContain(r.ThisMembers, m => m.Name == "total");
        Assert.DoesNotContain(r.Locals, m => m.Name == "total");
        Assert.True(r.DroppedRecoveryArtifacts > 0);
        Assert.Contains(r.ThisMembers, m => m.Name == "GetOrderAsync"); // genuine members declared elsewhere remain
    }

    [Fact]
    public async Task KeywordNamedParametersAreEscaped()
    {
        var caret = TestUtil.After(_service.Text, "return copy", atStart: true);
        var r = await SemanticAnalyzer.AnalyzeAsync(_source.Find(ServicePath, out _)!, TestUtil.SampleAt(_service, ServicePath, caret),
            VisibilityPolicy.EditorSnapshot, new SemanticConfig(), TestContext.Current.CancellationToken);
        Assert.Contains(r.Parameters, p => p.Name == "@event");
        Assert.Contains("@event:Order", r.Prompt);
        Assert.Contains(r.Locals, l => l.Name == "copy" && l.Type == "Guid");
        Assert.Empty(r.Leakage!.Violations);
    }

    [Fact]
    public void IdentifiersIgnoreStringsCommentsAndContextualKeywords()
    {
        var ids = CaretExtractor.Identifiers("await logger.LogInformation(\"Handling {Id}\", order.Id); // note var").ToList();
        Assert.Equal(["logger", "LogInformation", "order", "Id"], ids);
    }
}

public class BeingTypedTests
{
    [Theory]
    [InlineData("if (map.TryGetValue(key, out var su", "bscriptions)) { }")]
    [InlineData("foreach (var it", "em in list) { }")]
    [InlineData("try { } catch (Exception ex", "ception) { }")]
    [InlineData("var q = from it", "em in list select item;")]
    public async Task PartiallyTypedDeclarationsAreNotFacts(string before, string target)
    {
        var text = "using System;\nusing System.Linq;\nusing System.Collections.Generic;\nclass C\n{\n    void M(Dictionary<string, int> map, string key, List<int> list)\n    {\n        " + before + target + "\n    }\n}\n";
        var doc = SourceDocument.FromText(text);
        using var src = AdhocDocumentSource.Create(TestUtil.FixtureRepo, [("src/C.cs", null, text)]);
        var caret = text.IndexOf(before, StringComparison.Ordinal) + before.Length;
        var sample = TestUtil.SampleAt(doc, "src/C.cs", caret);
        foreach (var policy in new[] { VisibilityPolicy.EditorSnapshot, VisibilityPolicy.StrictPrefix })
        foreach (var engine in new[] { "auto", "fork" })
        {
            var r = await SemanticAnalyzer.AnalyzeAsync(src.Find("src/C.cs", out _)!, sample, policy, new SemanticConfig { Engine = engine }, TestContext.Current.CancellationToken);
            Assert.Empty(r.Leakage!.Violations);
            var partial = before[(before.LastIndexOfAny([' ', '(']) + 1)..];
            Assert.DoesNotContain(r.Locals, l => l.Name == partial);
            Assert.Contains(r.Parameters, p => p.Name == "map");
        }
    }
}

public class ContextTypeTests
{
    const string Code = """
        namespace Shop2;
        public sealed class Customer { public string Name { get; set; } = ""; public int Orders { get; set; } public void Rename(string name) { } }
        public sealed class Invoice { public Invoice(int number) { Number = number; } public int Number { get; } public decimal Total() => 0m; }
        public interface IClock { System.DateTime Now(); }
        public class Billing(IClock clock)
        {
            public void Run(Customer customer)
            {
                var name = customer.Name;
                var inv = new Invoice(42);
            }
        }
        """;

    static async Task<SemanticRecord> At(string marker, bool atStart, string engine = "auto")
    {
        var doc = SourceDocument.FromText(Code);
        using var src = AdhocDocumentSource.Create(TestUtil.FixtureRepo, [("src/Billing.cs", null, Code)]);
        var caret = TestUtil.After(Code, marker, atStart);
        return await SemanticAnalyzer.AnalyzeAsync(src.Find("src/Billing.cs", out _)!, TestUtil.SampleAt(doc, "src/Billing.cs", caret),
            VisibilityPolicy.EditorSnapshot, new SemanticConfig { Engine = engine }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("fork")]
    public async Task TypesOfInScopeSymbolsAreListedWithMembers(string engine)
    {
        var r = await At("var inv", atStart: true, engine);
        var customer = r.ContextTypes.Single(t => t.Name == "Customer");
        Assert.Equal("parameter", customer.Source);
        Assert.Contains(customer.Members, m => m.Name == "Rename");
        Assert.Contains(customer.Members, m => m.Name == "Orders");
        var clock = r.ContextTypes.Single(t => t.Name == "IClock");
        Assert.Contains(clock.Members, m => m.Name == "Now");
        Assert.Contains("TYPE Customer:", r.Prompt);
        // Invoice is used only inside the hidden target line: it must not be selected.
        Assert.DoesNotContain(r.ContextTypes, t => t.Name == "Invoice");
        Assert.Empty(r.Leakage!.Violations);
    }

    [Fact]
    public async Task TypeNamedInVisiblePrefixIsListedWithConstructor()
    {
        var r = await At("new Invoice(", atStart: false);
        var inv = r.ContextTypes.Single(t => t.Name == "Invoice");
        Assert.Equal("prefix", inv.Source);
        Assert.Contains(inv.Members, m => m.Kind == "constructor" && m.Signature == "new(int number)");
    }

    [Fact]
    public async Task BlockCanBeDisabled()
    {
        var doc = SourceDocument.FromText(Code);
        using var src = AdhocDocumentSource.Create(TestUtil.FixtureRepo, [("src/Billing.cs", null, Code)]);
        var r = await SemanticAnalyzer.AnalyzeAsync(src.Find("src/Billing.cs", out _)!, TestUtil.SampleAt(doc, "src/Billing.cs", TestUtil.After(Code, "var inv", true)),
            VisibilityPolicy.EditorSnapshot, new SemanticConfig { MaxContextTypes = 0 }, TestContext.Current.CancellationToken);
        Assert.Empty(r.ContextTypes);
    }
}

public class PatternCombinatorTests
{
    [Fact]
    public async Task PatternCombinatorKeywordIsNeverALocal()
    {
        var code = "enum K { A, B, C }\nclass C\n{\n    static bool M(K k) => k is\n        K.A or\n        K.B or\n        K.C;\n}\n";
        var doc = SourceDocument.FromText(code);
        using var src = AdhocDocumentSource.Create(TestUtil.FixtureRepo, [("src/K.cs", null, code)]);
        var caret = TestUtil.After(code, "K.C;", atStart: true);
        foreach (var engine in new[] { "auto", "fork" })
        {
            var r = await SemanticAnalyzer.AnalyzeAsync(src.Find("src/K.cs", out _)!, TestUtil.SampleAt(doc, "src/K.cs", caret),
                VisibilityPolicy.EditorSnapshot, new SemanticConfig { Engine = engine }, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(r.Locals, l => l.Name == "or");
            Assert.Empty(r.Leakage!.Violations);
        }
    }
}

public class LeakAuditDegradeTests
{
    [Fact]
    public void ValidatorStillRejectsAnyShippedViolation()
    {
        // The analyzer degrades violating records itself; the schema/validator gate must still reject a record that carries one.
        DatasetValidator.SchemaDirectory = Path.Combine(AppContext.BaseDirectory, "schemas");
        // Private registry: the validator registers the same $id globally.
        var text = File.ReadAllText(Path.Combine(DatasetValidator.SchemaDirectory, "flc-semantic.v1.schema.json")).Replace("\"$id\"", "\"$comment\"");
        var schema = Json.Schema.JsonSchema.FromText(text);
        var bad = FlcJson.Serialize(new SemanticRecord { SampleId = new string('a', 32), VisibilityPolicy = "editor_snapshot", Status = "resolved",
            Leakage = new LeakageAudit { TargetIdentifiers = [], CoveredTargetIdentifiers = [], Violations = ["x"] } });
        using var doc = System.Text.Json.JsonDocument.Parse(bad);
        Assert.False(schema.Evaluate(doc.RootElement).IsValid);
    }
}
