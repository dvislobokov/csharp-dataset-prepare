using FlcDataset.Cli;
using FlcDataset.Core;
using FlcDataset.Extraction;

namespace FlcDataset.Tests;

public class PipelineTests
{
    [Fact]
    public void DiscoveryAppliesReasonCodedFilters()
    {
        var cfg = TestUtil.AllCarets();
        var repo = RepositoryInfo.Inspect(TestUtil.FixtureRepo, cfg);
        Assert.Null(repo.Revision); // not a git checkout root -> never inherits a parent repository SHA
        Assert.Equal("MIT", repo.License.Spdx);
        Assert.True(repo.License.Allowed);
        var files = new Discoverer(cfg, repo).Discover().ToDictionary(f => f.Record.RelativePath, f => f.Record);
        Assert.Equal("generated_path", files["src/Shop/Generated.g.cs"].SkipReason);
        Assert.Equal("generated_marker", files["src/Shop/Tool.cs"].SkipReason);
        // Ordinal path order: Order.cs precedes OrderCopy.cs, so the copy is the duplicate.
        Assert.Equal("exact_duplicate", files["src/Shop/OrderCopy.cs"].SkipReason);
        Assert.Equal("src/Shop/Order.cs", files["src/Shop/OrderCopy.cs"].DuplicateOf);
        Assert.Equal("accepted", files["src/Shop/OrderService.cs"].Status);
        Assert.True(files["tests/Shop.Tests/OrderTests.cs"].IsTest);
        Assert.False(files["src/Shop/OrderService.cs"].IsTest);
        Assert.Equal("src/Shop/Shop.csproj", files["src/Shop/OrderService.cs"].Project);
    }

    [Fact]
    public void UnknownLicenseIsNotSilentlyAccepted()
    {
        var dir = TestUtil.TempDir();
        File.WriteAllText(Path.Combine(dir, "A.cs"), "class A { }\n");
        var cfg = TestUtil.AllCarets() with { Discovery = new DiscoveryConfig { Include = ["**/*.cs"] } };
        var repo = RepositoryInfo.Inspect(dir, cfg);
        Assert.False(repo.License.Allowed);
        Assert.Equal("no_license_file", repo.License.Reason);
        var f = new Discoverer(cfg, repo).Discover().Single();
        Assert.Equal("license_not_allowed", f.Record.SkipReason);
        var allowed = new Discoverer(cfg with { License = new LicenseConfig { AllowUnknown = true } }, RepositoryInfo.Inspect(dir, cfg)).Discover().Single();
        Assert.Equal("accepted", allowed.Record.Status);
    }

    [Fact]
    public void SecretFilesAreSkippedFromCorpus()
    {
        var dir = TestUtil.TempDir();
        File.WriteAllText(Path.Combine(dir, "Keys.cs"), "class K { const string P = \"-----BEGIN RSA PRIVATE KEY-----\"; }\n");
        var cfg = TestUtil.AllCarets() with { Discovery = new DiscoveryConfig { Include = ["**/*.cs"] }, License = new LicenseConfig { AllowUnknown = true } };
        var f = new Discoverer(cfg, RepositoryInfo.Inspect(dir, cfg)).Discover().Single();
        Assert.Equal("secret_detected", f.Record.SkipReason);
    }

    [Fact]
    public void SplitsAreAssignedPerProjectFamily()
    {
        var cfg = TestUtil.AllCarets() with { Split = new SplitConfig { Overrides = new() { ["Shop"] = "eval" } } };
        var d = new Discoverer(cfg, RepositoryInfo.Inspect(TestUtil.FixtureRepo, cfg));
        var a = d.SplitOf("src/Shop/Order.cs");
        var b = d.SplitOf("tests/Shop.Tests/OrderTests.cs");
        Assert.Equal("Shop", a.Group);
        Assert.Equal(a, b); // tests of a service share its family and split
        Assert.Equal("eval", a.Split);
    }

    static async Task<string> Run(int workers, DatasetConfig? cfg = null, bool gzip = false)
    {
        var dir = TestUtil.TempDir();
        await ExtractionPipeline.RunAsync(new PipelineOptions { RepoPath = TestUtil.FixtureRepo, OutputDir = dir, Config = cfg ?? TestUtil.AllCarets(), Workers = workers, Gzip = gzip });
        return dir;
    }

    [Fact]
    public async Task SequentialAndParallelRunsAreByteIdentical()
    {
        var a = await Run(1);
        var b = await Run(4);
        var c = await Run(4);
        foreach (var r in new[] { Commands.CompareDirs(a, b), Commands.CompareDirs(a, c) })
            foreach (var (file, v) in r)
                Assert.True((bool)v.GetType().GetProperty("identical")!.GetValue(v)!, file);
        Assert.True(File.Exists(Path.Combine(a, "run-manifest.json")));
        Assert.Empty(Directory.EnumerateFiles(a, "*.partial"));
    }

    [Fact]
    public async Task SemanticPipelineIsDeterministicAndOnlyEnrichesAdmittedSamples()
    {
        DatasetValidator.SchemaDirectory = Path.Combine(AppContext.BaseDirectory, "schemas");
        var cfg = TestUtil.AllCarets() with
        {
            Semantic = new SemanticConfig { SubsetFraction = 1.0 },
            Sampling = TestUtil.AllCarets().Sampling with { DropDuplicateLineTargets = true },
        };
        var files = new Discoverer(cfg, RepositoryInfo.Inspect(TestUtil.FixtureRepo, cfg)).Discover()
            .Where(f => f.Document is not null).Select(f => (f.Record.RelativePath, f.Record.Project, f.Document!.Text)).ToList();
        async Task<(string Dir, PipelineResult R)> RunSem(int workers)
        {
            using var src = FlcDataset.Semantics.AdhocDocumentSource.Create(TestUtil.FixtureRepo, files);
            var dir = TestUtil.TempDir();
            var r = await ExtractionPipeline.RunAsync(new PipelineOptions
            {
                RepoPath = TestUtil.FixtureRepo, OutputDir = dir, Config = cfg, Workers = workers, Mode = "semantic_best_effort",
                Enricher = new FlcDataset.Semantics.SemanticEnricher(src, cfg, "semantic_best_effort"),
            }, TestContext.Current.CancellationToken);
            return (dir, r);
        }
        var (a, ra) = await RunSem(1);
        var (b, _) = await RunSem(4);
        foreach (var (file, v) in Commands.CompareDirs(a, b))
            Assert.True((bool)v.GetType().GetProperty("identical")!.GetValue(v)!, file);
        Assert.True(ra.Summary.Samples["dropped_duplicate"] > 0);
        Assert.Equal(ra.Summary.Samples["written"], ra.Summary.Semantic["attempted"]);
        var report = new DatasetValidator().Validate(a, TestUtil.FixtureRepo);
        Assert.True(report.Ok, string.Join("\n", report.FailureExamples));
        Assert.True(report.Checked["semantic.no_leak_violations"] > 0);
    }

    [Fact]
    public async Task ValidatorAcceptsCleanDatasetAndCatchesTampering()
    {
        DatasetValidator.SchemaDirectory = Path.Combine(AppContext.BaseDirectory, "schemas");
        var dir = await Run(2, gzip: false);
        var ok = new DatasetValidator().Validate(dir, TestUtil.FixtureRepo);
        Assert.True(ok.Ok, string.Join("\n", ok.FailureExamples));
        Assert.True(ok.Checked["source.reconstruction"] > 0);

        var samples = Path.Combine(dir, "samples.jsonl");
        var lines = File.ReadAllLines(samples);
        var first = FlcJson.Deserialize<FlcSampleRecord>(lines[0]);
        lines[0] = FlcJson.Serialize(first with { TargetText = first.TargetText + "x" });
        File.WriteAllLines(samples, lines);
        var bad = new DatasetValidator().Validate(dir, TestUtil.FixtureRepo);
        Assert.False(bad.Ok);
        Assert.Contains("manifest.output_checksum", bad.Failures.Keys);
        Assert.Contains("source.reconstruction", bad.Failures.Keys);
    }

    [Fact]
    public async Task GzipOutputIsValid()
    {
        DatasetValidator.SchemaDirectory = Path.Combine(AppContext.BaseDirectory, "schemas");
        var dir = await Run(2, gzip: true);
        Assert.True(File.Exists(Path.Combine(dir, "samples.jsonl.gz")));
        Assert.True(new DatasetValidator().Validate(dir, TestUtil.FixtureRepo).Ok);
    }

    [Fact]
    public async Task CancellationLeavesNoFinalShards()
    {
        var dir = TestUtil.TempDir();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ExtractionPipeline.RunAsync(new PipelineOptions { RepoPath = TestUtil.FixtureRepo, OutputDir = dir, Config = TestUtil.AllCarets(), Workers = 2 }, cts.Token));
        Assert.False(File.Exists(Path.Combine(dir, "samples.jsonl")));
        Assert.False(File.Exists(Path.Combine(dir, "run-manifest.json")));
    }

    [Fact]
    public void WriterWithoutCompleteNeverProducesFinalFile()
    {
        var dir = TestUtil.TempDir();
        var path = Path.Combine(dir, "x.jsonl");
        using (var w = new JsonlWriter<int>(path)) w.Write(1);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".partial"));
        using var w2 = new JsonlWriter<int>(path);
        w2.Write(2);
        var info = w2.Complete();
        Assert.Equal(1, info.Records);
        Assert.Equal(Hashing.Sha256File(path), info.Sha256);
    }

    [Fact]
    public async Task ExtractRefusesToOverwriteWithoutFlag()
    {
        var dir = TestUtil.TempDir();
        File.WriteAllText(Path.Combine(dir, "keep.txt"), "x");
        var cfgPath = Path.Combine(dir, "cfg.json");
        File.WriteAllText(cfgPath, FlcJson.Serialize(TestUtil.AllCarets()));
        var code = await Commands.Extract(new ExtractArgs { Repo = TestUtil.FixtureRepo, Config = cfgPath, Out = dir, Mode = "syntax_only" }, default);
        Assert.Equal(2, code);
        Assert.True(File.Exists(Path.Combine(dir, "keep.txt")));
    }

    [Fact]
    public void ShippedConfigsDeserialize()
    {
        var cfg = DatasetConfig.Load(Path.Combine(AppContext.BaseDirectory, "configs", "eshop.pilot.json"));
        Assert.Equal("github.com/dotnet/eShop", cfg.RepositoryId);
        Assert.Equal("eShop.Web.slnf", cfg.Semantic.Solution);
        Assert.Equal("eval", cfg.Split.Overrides["Basket"]);
        Assert.Equal(cfg.Hash(), DatasetConfig.Load(Path.Combine(AppContext.BaseDirectory, "configs", "eshop.pilot.json")).Hash());
    }
}

public class LicenseDetectionTests
{
    [Theory]
    [InlineData("BSD 3-Clause License\n\nRedistribution and use in source and binary forms, with or without modification, are permitted...\n3. Neither the name of the copyright holder nor the names of its contributors may be used...\nTHIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS \"AS IS\" AND ANY EXPRESS", "BSD-3-Clause")]
    [InlineData("Redistribution and use in source and binary forms, with or without modification...\nTHIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS \"AS IS\"", "BSD-2-Clause")]
    [InlineData("The MIT License (MIT)\n\nPermission is hereby granted, free of charge, to any person...\nTHE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY", "MIT")]
    [InlineData("All rights reserved. Proprietary.", null)]
    public void DetectsCommonLicenses(string text, string? spdx)
    {
        var dir = TestUtil.TempDir();
        File.WriteAllText(Path.Combine(dir, "LICENSE"), text);
        var info = RepositoryInfo.DetectLicense(dir, new LicenseConfig());
        Assert.Equal(spdx, info.Spdx);
        Assert.Equal(spdx is not null, info.Allowed);
    }
}
