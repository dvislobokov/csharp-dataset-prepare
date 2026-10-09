using FlcDataset.Core;

namespace FlcDataset.Tests;

public class ScaleTests
{
    static string Write(string name, string content)
    {
        var p = Path.Combine(TestUtil.TempDir(), name);
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void TxtManifestValidatesAndNormalizes()
    {
        var p = Write("m.txt", """
            # comment
            https://github.com/dotnet/eShop.git
            https://github.com/dotnet/eshop
            http://github.com/a/b
            https://user:token@github.com/a/b
            https://evil.example.com/a/b
            https://github.com/a/../b
            https://github.com/a/b;rm -rf
            https://gitlab.com/group/sub/proj
            """);
        var r = new RepositoryManifestReader().Read(p);
        Assert.Equal(["github.com/dotnet/eShop", "gitlab.com/group/sub/proj"], r.Entries.Select(e => e.RepositoryId));
        Assert.Equal(["duplicate_repository", "scheme_not_allowed", "credentials_in_url", "host_not_allowed", "invalid_repository_path", "unsafe_characters"],
            r.Rejected.Select(x => x.Reason));
    }

    [Fact]
    public void CsvAndJsonlManifests()
    {
        var csv = Write("m.csv", "url,revision,license,priority,fork_of\nhttps://github.com/a/b,0123456789abcdef0123456789abcdef01234567,MIT,5,\nhttps://github.com/c/b,,,1,https://github.com/a/b\nhttps://github.com/x/y,,,notanint,\n");
        var r = new RepositoryManifestReader().Read(csv);
        Assert.Equal(2, r.Entries.Count);
        Assert.Equal(5, r.Entries[0].Priority);
        Assert.Equal("MIT", r.Entries[0].License);
        Assert.Equal("github.com/a/b", r.Entries[1].ForkOf);
        Assert.Equal("invalid_priority", r.Rejected.Single().Reason);

        var jsonl = Write("m.jsonl", "{\"url\":\"https://github.com/a/b\",\"repository_id\":\"\",\"revision\":\"main\",\"priority\":2}\nnot json\n");
        var j = new RepositoryManifestReader().Read(jsonl);
        Assert.Equal("main", j.Entries.Single().Revision);
        Assert.Equal("invalid_json", j.Rejected.Single().Reason);
    }

    [Fact]
    public void JobStoreEnforcesTransitionsAndResumes()
    {
        var path = Path.Combine(TestUtil.TempDir(), "jobs.jsonl");
        var store = new JsonlJobStore(path);
        store.Save(new RepoJob { RepositoryId = "r1", State = RepoJobState.Pending });
        store.Save(new RepoJob { RepositoryId = "r1", State = RepoJobState.Cloning, Attempts = 1 });
        store.Save(new RepoJob { RepositoryId = "r2", State = RepoJobState.Pending });
        store.Save(new RepoJob { RepositoryId = "r2", State = RepoJobState.Skipped, Error = "license_not_allowed" });
        Assert.Throws<InvalidOperationException>(() => store.Save(new RepoJob { RepositoryId = "r1", State = RepoJobState.Complete }));
        Assert.Throws<InvalidOperationException>(() => store.Save(new RepoJob { RepositoryId = "r3", State = RepoJobState.Complete }));
        File.AppendAllText(path, "{\"repository_id\":\"r4\",\"sta"); // torn write from a crash
        var reopened = new JsonlJobStore(path);
        Assert.Equal(RepoJobState.Cloning, reopened.Load()["r1"].State);
        Assert.Equal(["r1"], reopened.Resumable(3).Select(j => j.RepositoryId));
    }

    [Fact]
    public void ForksAndNearCopiesShareSplitGroups()
    {
        var g = new RepositoryGrouper();
        g.AddRepository("github.com/a/orig");
        g.AddFork("github.com/z/fork", "github.com/a/orig");
        g.AddContentOverlap(new Dictionary<string, HashSet<string>>
        {
            ["github.com/a/orig"] = ["h1", "h2", "h3", "h4"],
            ["github.com/m/copy"] = ["h1", "h2", "h3", "h9"],   // Jaccard 3/5 = 0.6
            ["github.com/q/other"] = ["h1", "x1", "x2", "x3"],  // Jaccard 1/7
        }, threshold: 0.5);
        Assert.Equal(g.GroupOf("github.com/a/orig"), g.GroupOf("github.com/z/fork"));
        Assert.Equal(g.GroupOf("github.com/a/orig"), g.GroupOf("github.com/m/copy"));
        Assert.NotEqual(g.GroupOf("github.com/a/orig"), g.GroupOf("github.com/q/other"));
        var split = g.SplitOf("github.com/a/orig", 1, 0.3, 0.3);
        Assert.Equal(split, g.SplitOf("github.com/z/fork", 1, 0.3, 0.3));
        Assert.Equal(split, g.SplitOf("github.com/m/copy", 1, 0.3, 0.3));
    }
}
