using System.Globalization;

namespace FlcDataset.Core;

// Scale-ready contracts for the future ~34k-repository manifest. No downloader is implemented in this phase.

/// <summary>One user-supplied manifest entry. Nothing here is assumed to be public, buildable or licensed.</summary>
public sealed record RepositoryManifestEntry
{
    public required string Url { get; init; }
    public required string RepositoryId { get; init; }
    public string? Revision { get; init; }
    public string? License { get; init; }
    public string? Provenance { get; init; }
    public List<string> Include { get; init; } = [];
    public List<string> Exclude { get; init; } = [];
    public int Priority { get; init; }
    /// <summary>Upstream repository id when this entry is a known fork (used for split grouping).</summary>
    public string? ForkOf { get; init; }
    public int SourceLine { get; init; }
}

public sealed record ManifestRejection(int Line, string Raw, string Reason);

public sealed record ManifestReadResult(List<RepositoryManifestEntry> Entries, List<ManifestRejection> Rejected);

public sealed record ManifestPolicy
{
    public List<string> AllowedSchemes { get; init; } = ["https"];
    public List<string> AllowedHosts { get; init; } = ["github.com", "gitlab.com", "bitbucket.org", "dev.azure.com"];
}

public interface IRepositoryManifestReader
{
    ManifestReadResult Read(string path);
}

/// <summary>
/// Reads txt (one URL per line, '#' comments), csv (header with url[,revision,license,priority,fork_of,provenance]) or jsonl
/// (RepositoryManifestEntry fields). URLs are validated: allowlisted scheme/host, no credentials, no path traversal, no
/// shell metacharacters. Duplicate repository ids keep the first entry; every rejection is reason-coded.
/// </summary>
public sealed class RepositoryManifestReader(ManifestPolicy? policy = null) : IRepositoryManifestReader
{
    readonly ManifestPolicy _policy = policy ?? new ManifestPolicy();

    public ManifestReadResult Read(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        var lines = File.ReadAllLines(path);
        var raw = ext switch
        {
            ".txt" => ReadTxt(lines),
            ".csv" => ReadCsv(lines),
            ".jsonl" => ReadJsonl(lines),
            _ => throw new NotSupportedException($"Unsupported manifest format '{ext}' (txt, csv, jsonl)"),
        };
        var entries = new List<RepositoryManifestEntry>();
        var rejected = new List<ManifestRejection>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (line, text, entry, error) in raw)
        {
            if (error is not null) { rejected.Add(new ManifestRejection(line, text, error)); continue; }
            var (id, reason) = Normalize(entry!.Url);
            if (reason is not null) { rejected.Add(new ManifestRejection(line, text, reason)); continue; }
            if (entry.Revision is { } rev && !IsHexSha(rev) && rev.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '/')))
            { rejected.Add(new ManifestRejection(line, text, "invalid_revision")); continue; }
            if (!seen.Add(id!)) { rejected.Add(new ManifestRejection(line, text, "duplicate_repository")); continue; }
            entries.Add(entry with { RepositoryId = id!, SourceLine = line, ForkOf = entry.ForkOf is null ? null : Normalize(entry.ForkOf).Id ?? entry.ForkOf });
        }
        return new ManifestReadResult(entries, rejected);
    }

    static bool IsHexSha(string s) => s.Length is 40 or 64 && s.All(Uri.IsHexDigit);

    /// <summary>Canonical id "host/owner/name" (lower-case host, no .git suffix) or a rejection reason.</summary>
    public (string? Id, string? Reason) Normalize(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return (null, "empty_url");
        if (url.IndexOfAny([';', '|', '&', '$', '`', '\n', '\r', ' ', '"', '\'', '\\']) >= 0) return (null, "unsafe_characters");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return (null, "not_absolute_url");
        if (!_policy.AllowedSchemes.Contains(uri.Scheme)) return (null, "scheme_not_allowed");
        if (!string.IsNullOrEmpty(uri.UserInfo)) return (null, "credentials_in_url");
        if (!_policy.AllowedHosts.Contains(uri.Host.ToLowerInvariant())) return (null, "host_not_allowed");
        var segs = uri.AbsolutePath.Trim('/').Split('/');
        if (segs.Length < 2 || segs.Any(s => s is "" or "." or "..")) return (null, "invalid_repository_path");
        var name = segs[^1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segs[^1][..^4] : segs[^1];
        return ($"{uri.Host.ToLowerInvariant()}/{string.Join('/', segs[..^1])}/{name}", null);
    }

    static IEnumerable<(int, string, RepositoryManifestEntry?, string?)> ReadTxt(string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            yield return (i + 1, t, new RepositoryManifestEntry { Url = t, RepositoryId = "" }, null);
        }
    }

    static IEnumerable<(int, string, RepositoryManifestEntry?, string?)> ReadCsv(string[] lines)
    {
        if (lines.Length == 0) yield break;
        var header = lines[0].Split(',').Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(string n) => header.IndexOf(n);
        if (Col("url") < 0) { yield return (1, lines[0], null, "csv_missing_url_column"); yield break; }
        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var f = lines[i].Split(',').Select(x => x.Trim()).ToArray(); // simple CSV: fields must not contain commas
            string? Get(string n) => Col(n) is var c && c >= 0 && c < f.Length && f[c].Length > 0 ? f[c] : null;
            if (Get("priority") is { } pr && !int.TryParse(pr, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            { yield return (i + 1, lines[i], null, "invalid_priority"); continue; }
            yield return (i + 1, lines[i], new RepositoryManifestEntry
            {
                Url = Get("url") ?? "", RepositoryId = "", Revision = Get("revision"), License = Get("license"), ForkOf = Get("fork_of"),
                Provenance = Get("provenance"), Priority = Get("priority") is { } p ? int.Parse(p, CultureInfo.InvariantCulture) : 0,
            }, null);
        }
    }

    static IEnumerable<(int, string, RepositoryManifestEntry?, string?)> ReadJsonl(string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            RepositoryManifestEntry? e = null;
            string? err = null;
            try { e = FlcJson.Deserialize<RepositoryManifestEntry>(lines[i]); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException) { err = "invalid_json"; }
            yield return (i + 1, lines[i], e, err);
        }
    }
}

/// <summary>Per-repository job lifecycle for the later ingestion workflow.</summary>
public enum RepoJobState { Pending, Cloning, Ready, Processing, Complete, Skipped, Failed }

public sealed record RepoJob
{
    public required string RepositoryId { get; init; }
    public required RepoJobState State { get; init; }
    public int Attempts { get; init; }
    public string? Error { get; init; }
    public string? ResolvedRevision { get; init; }
    public string? OutputShard { get; init; }
    public string? OutputSha256 { get; init; }
    public string UpdatedUtc { get; init; } = DateTimeOffset.UtcNow.ToString("O");
}

public interface IJobStore
{
    IReadOnlyDictionary<string, RepoJob> Load();
    void Save(RepoJob job);
}

/// <summary>
/// Append-only JSONL job log: last record per repository wins, so an interrupted run resumes by replaying the log.
/// Each append is a single write + flush; a torn final line is ignored on load.
/// </summary>
public sealed class JsonlJobStore(string path) : IJobStore
{
    static readonly HashSet<(RepoJobState, RepoJobState)> Allowed =
    [
        (RepoJobState.Pending, RepoJobState.Cloning), (RepoJobState.Pending, RepoJobState.Skipped),
        (RepoJobState.Cloning, RepoJobState.Ready), (RepoJobState.Cloning, RepoJobState.Failed),
        (RepoJobState.Ready, RepoJobState.Processing), (RepoJobState.Processing, RepoJobState.Complete),
        (RepoJobState.Processing, RepoJobState.Failed), (RepoJobState.Failed, RepoJobState.Pending),
        // An interrupted run leaves cloning/processing behind; resuming restarts them.
        (RepoJobState.Cloning, RepoJobState.Pending), (RepoJobState.Processing, RepoJobState.Pending),
    ];

    public static bool IsAllowed(RepoJobState from, RepoJobState to) => Allowed.Contains((from, to));

    public IReadOnlyDictionary<string, RepoJob> Load()
    {
        var map = new Dictionary<string, RepoJob>(StringComparer.Ordinal);
        if (!File.Exists(path)) return map;
        foreach (var line in File.ReadLines(path))
        {
            try { var j = FlcJson.Deserialize<RepoJob>(line); map[j.RepositoryId] = j; }
            catch (System.Text.Json.JsonException) { /* torn tail from a crash */ }
        }
        return map;
    }

    public void Save(RepoJob job)
    {
        var current = Load().GetValueOrDefault(job.RepositoryId);
        if (current is not null && current.State != job.State && !IsAllowed(current.State, job.State))
            throw new InvalidOperationException($"Illegal transition {current.State} -> {job.State} for {job.RepositoryId}");
        if (current is null && job.State != RepoJobState.Pending) throw new InvalidOperationException("Jobs start as Pending");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        var bytes = System.Text.Encoding.UTF8.GetBytes(FlcJson.Serialize(job) + "\n");
        fs.Write(bytes);
        fs.Flush(flushToDisk: true);
    }

    /// <summary>Jobs to (re)run after a restart: pending, interrupted (cloning/processing) and retryable failures.</summary>
    public IEnumerable<RepoJob> Resumable(int maxAttempts) => Load().Values
        .Where(j => j.State is RepoJobState.Pending or RepoJobState.Cloning or RepoJobState.Ready or RepoJobState.Processing
                    || (j.State == RepoJobState.Failed && j.Attempts < maxAttempts))
        .OrderBy(j => j.RepositoryId, StringComparer.Ordinal);
}

/// <summary>
/// Repository-group split assignment: forks join their upstream's group, and repositories sharing many identical files
/// (by sha256) are merged into one cluster, so near-copies never straddle train/eval/test. Splits are assigned per group
/// before any caret expansion.
/// </summary>
public sealed class RepositoryGrouper
{
    readonly Dictionary<string, string> _parent = new(StringComparer.Ordinal);

    string FindRoot(string x)
    {
        if (!_parent.TryGetValue(x, out var p)) { _parent[x] = x; return x; }
        if (p == x) return x;
        var r = FindRoot(p);
        _parent[x] = r;
        return r;
    }

    void Union(string a, string b)
    {
        var (ra, rb) = (FindRoot(a), FindRoot(b));
        if (ra == rb) return;
        // Deterministic representative: ordinal-smaller id.
        if (string.CompareOrdinal(ra, rb) < 0) _parent[rb] = ra; else _parent[ra] = rb;
    }

    public void AddRepository(string id) => FindRoot(id);

    public void AddFork(string fork, string upstream) => Union(fork, upstream);

    /// <summary>Merge repositories whose file-hash sets overlap by at least <paramref name="threshold"/> (Jaccard).</summary>
    public void AddContentOverlap(IReadOnlyDictionary<string, HashSet<string>> fileHashesByRepo, double threshold)
    {
        var owners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (repo, hashes) in fileHashesByRepo.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            AddRepository(repo);
            foreach (var h in hashes) (owners.TryGetValue(h, out var l) ? l : owners[h] = []).Add(repo);
        }
        var shared = new Dictionary<(string, string), int>();
        foreach (var l in owners.Values.Where(l => l.Count > 1))
            for (int i = 0; i < l.Count; i++)
                for (int j = i + 1; j < l.Count; j++)
                    shared[(l[i], l[j])] = shared.GetValueOrDefault((l[i], l[j])) + 1;
        foreach (var ((a, b), n) in shared)
        {
            var union = fileHashesByRepo[a].Count + fileHashesByRepo[b].Count - n;
            if (union > 0 && n / (double)union >= threshold) Union(a, b);
        }
    }

    public string GroupOf(string id) => FindRoot(id);

    public string SplitOf(string id, ulong seed, double evalFraction, double testFraction)
    {
        var u = Hashing.Uniform(seed.ToString(CultureInfo.InvariantCulture), "repo_split", GroupOf(id));
        return u < evalFraction ? "eval" : u < evalFraction + testFraction ? "test" : "train";
    }
}
