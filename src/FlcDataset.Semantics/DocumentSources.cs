using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FlcDataset.Core;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace FlcDataset.Semantics;

/// <summary>Maps repository-relative paths to Roslyn documents inside a loaded (immutable) solution snapshot.</summary>
public interface ISemanticDocumentSource : IDisposable
{
    string Kind { get; }
    Document? Find(string relativePath, out string? reason);
    /// <summary>Warm the project compilation once (cache); returns true when it was already cached.</summary>
    Task<bool> WarmAsync(Project project, CancellationToken ct);
    Dictionary<string, object?> Describe();
}

public abstract class DocumentSourceBase : ISemanticDocumentSource
{
    protected readonly string Root;
    protected Solution Solution = null!;
    protected readonly Dictionary<string, DocumentId> ByPath = new(StringComparer.Ordinal);
    protected readonly List<string> Diagnostics = [];
    readonly ConcurrentDictionary<ProjectId, Lazy<Task<Compilation?>>> _compilations = new();
    protected double LoadMs;

    protected DocumentSourceBase(string root) => Root = Path.GetFullPath(root);

    public abstract string Kind { get; }

    protected void IndexDocuments()
    {
        foreach (var project in Solution.Projects.Where(p => p.Language == LanguageNames.CSharp).OrderBy(p => p.Name, StringComparer.Ordinal))
        foreach (var d in project.Documents)
        {
            if (d.FilePath is null) continue;
            var rel = Path.GetRelativePath(Root, d.FilePath).Replace('\\', '/');
            if (rel.StartsWith("..", StringComparison.Ordinal)) continue;
            ByPath.TryAdd(rel, d.Id); // multi-targeted projects: first (ordinal by project name) wins
        }
    }

    public Document? Find(string relativePath, out string? reason)
    {
        reason = null;
        if (ByPath.TryGetValue(relativePath, out var id) && Solution.GetDocument(id) is { } doc) return doc;
        reason = "document_not_in_workspace";
        return null;
    }

    public async Task<bool> WarmAsync(Project project, CancellationToken ct)
    {
        // The factory may run on several threads; only the instance that wins TryAdd counts as the miss.
        var mine = new Lazy<Task<Compilation?>>(() => project.GetCompilationAsync(CancellationToken.None));
        var lazy = _compilations.GetOrAdd(project.Id, mine);
        await lazy.Value.WaitAsync(ct);
        return !ReferenceEquals(lazy, mine);
    }

    public virtual Dictionary<string, object?> Describe() => new()
    {
        ["semantic_source"] = Kind,
        ["projects_loaded"] = Solution.Projects.Count(),
        ["documents_indexed"] = ByPath.Count,
        ["workspace_load_ms"] = Math.Round(LoadMs, 1),
        ["workspace_diagnostics"] = Diagnostics.Count,
        ["workspace_diagnostics_sample"] = Diagnostics.Take(20).ToList(),
        ["projects"] = Solution.Projects.Select(p => new { p.Name, documents = p.Documents.Count(), references = p.MetadataReferences.Count }).OrderBy(p => p.Name).ToList(),
    };

    public virtual void Dispose() { }
}

/// <summary>
/// Trusted mode: evaluates MSBuild projects (imports and runs SDK/NuGet targets) via MSBuildWorkspace.
/// Requires an explicit trust opt-in and a prior restore. Never builds or runs the analyzed application.
/// </summary>
public sealed class MsBuildDocumentSource : DocumentSourceBase
{
    static int _registered;
    MSBuildWorkspace? _workspace;
    public static string? MsBuildPath { get; private set; }

    MsBuildDocumentSource(string root) : base(root) { }
    public override string Kind => "msbuild";

    /// <summary>Must run before any MSBuild/Workspaces.MSBuild type is loaded.</summary>
    public static void EnsureMsBuildRegistered(string workingDirectory)
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        if (MSBuildLocator.IsRegistered) return;
        var instances = MSBuildLocator.QueryVisualStudioInstances(new VisualStudioInstanceQueryOptions
        {
            DiscoveryTypes = DiscoveryType.DotNetSdk,
            WorkingDirectory = workingDirectory,
        }).OrderByDescending(i => i.Version).ToList();
        if (instances.Count == 0) throw new InvalidOperationException("no_dotnet_sdk_found_for_msbuild");
        MSBuildLocator.RegisterInstance(instances[0]);
        MsBuildPath = instances[0].MSBuildPath;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task<MsBuildDocumentSource> LoadAsync(string root, string solutionOrProject, RunLog log, CancellationToken ct)
    {
        var src = new MsBuildDocumentSource(root);
        var sw = Stopwatch.StartNew();
        var ws = MSBuildWorkspace.Create(new Dictionary<string, string> { ["Configuration"] = "Debug" });
        ws.RegisterWorkspaceFailedHandler(e =>
        {
            lock (src.Diagnostics) src.Diagnostics.Add($"{e.Diagnostic.Kind}: {e.Diagnostic.Message}");
        });
        src._workspace = ws;
        var path = Path.Combine(src.Root, solutionOrProject);
        log.Info("semantic", "workspace_load_start", new { path });
        src.Solution = path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            ? (await ws.OpenProjectAsync(path, cancellationToken: ct)).Solution
            : await ws.OpenSolutionAsync(path, cancellationToken: ct);
        src.IndexDocuments();
        src.LoadMs = sw.Elapsed.TotalMilliseconds;
        log.Info("semantic", "workspace_loaded", new { projects = src.Solution.Projects.Count(), documents = src.ByPath.Count, ms = Math.Round(src.LoadMs), diagnostics = src.Diagnostics.Count });
        return src;
    }

    public override Dictionary<string, object?> Describe()
    {
        var d = base.Describe();
        d["msbuild_path"] = MsBuildPath;
        return d;
    }

    public override void Dispose() => _workspace?.Dispose();
}

/// <summary>
/// Safe mode: no MSBuild evaluation, no restore, no code execution. Projects are reconstructed from discovered files,
/// csproj ProjectReference items (parsed as plain XML) and the installed shared-framework reference assemblies.
/// NuGet package references are unavailable, so symbols from packages resolve as error types (reason-coded downstream).
/// </summary>
public sealed class AdhocDocumentSource : DocumentSourceBase
{
    readonly AdhocWorkspace _workspace = new();
    public List<string> ReferenceDirectories { get; } = [];

    AdhocDocumentSource(string root) : base(root) { }
    public override string Kind => "adhoc";

    /// <summary>Implicit global usings of Microsoft.NET.Sdk (+ Web SDK). Injected as a synthetic, clearly named document.</summary>
    public const string ImplicitUsings = """
        global using global::System;
        global using global::System.Collections.Generic;
        global using global::System.IO;
        global using global::System.Linq;
        global using global::System.Net.Http;
        global using global::System.Threading;
        global using global::System.Threading.Tasks;
        """;

    public const string WebImplicitUsings = """
        global using global::System.Net.Http.Json;
        global using global::Microsoft.AspNetCore.Builder;
        global using global::Microsoft.AspNetCore.Hosting;
        global using global::Microsoft.AspNetCore.Http;
        global using global::Microsoft.AspNetCore.Routing;
        global using global::Microsoft.Extensions.Configuration;
        global using global::Microsoft.Extensions.DependencyInjection;
        global using global::Microsoft.Extensions.Hosting;
        global using global::Microsoft.Extensions.Logging;
        """;

    /// <param name="files">Accepted (relative path, project path or null, text) triples.</param>
    public static AdhocDocumentSource Create(string root, IEnumerable<(string Path, string? Project, string Text)> files, IEnumerable<string>? referenceDirs = null)
    {
        var src = new AdhocDocumentSource(root);
        var sw = Stopwatch.StartNew();
        var dirs = (referenceDirs ?? DefaultReferenceDirectories()).ToList();
        src.ReferenceDirectories.AddRange(dirs);
        var refs = dirs.SelectMany(d => Directory.EnumerateFiles(d, "*.dll"))
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .Where(IsManagedAssembly)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();

        var groups = files.GroupBy(f => f.Project ?? "(no-project)").OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        var ids = groups.ToDictionary(g => g.Key, g => ProjectId.CreateNewId(g.Key));
        var solution = src._workspace.CurrentSolution;
        foreach (var g in groups)
        {
            bool web = false;
            var projectRefs = new List<ProjectReference>();
            var projFull = Path.Combine(src.Root, g.Key);
            if (File.Exists(projFull))
            {
                try
                {
                    var xml = XDocument.Load(projFull);
                    web = xml.Root?.Attribute("Sdk")?.Value.Contains("Web", StringComparison.OrdinalIgnoreCase) == true;
                    foreach (var pr in xml.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
                    {
                        var inc = pr.Attribute("Include")?.Value.Replace('\\', '/');
                        if (inc is null) continue;
                        var target = Path.GetRelativePath(src.Root, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projFull)!, inc))).Replace('\\', '/');
                        // A csproj may list the same reference twice (e.g. under two conditions); Roslyn rejects duplicates.
                        if (ids.TryGetValue(target, out var pid) && target != g.Key && projectRefs.All(r => r.ProjectId != pid))
                            projectRefs.Add(new ProjectReference(pid));
                    }
                }
                catch (System.Xml.XmlException e) { src.Diagnostics.Add($"csproj_xml_error {g.Key}: {e.Message}"); }
            }
            var name = Path.GetFileNameWithoutExtension(g.Key);
            var parse = FlcDataset.Extraction.CaretExtractor.ParseOptions;
            var docs = g.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => DocumentInfo.Create(
                DocumentId.CreateNewId(ids[g.Key], f.Path), Path.GetFileName(f.Path),
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(f.Text), VersionStamp.Default, Path.Combine(src.Root, f.Path))),
                filePath: Path.Combine(src.Root, f.Path))).ToList();
            docs.Add(DocumentInfo.Create(DocumentId.CreateNewId(ids[g.Key], "implicit"), "__flc_implicit_usings.g.cs",
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(ImplicitUsings + "\n" + (web ? WebImplicitUsings : "")), VersionStamp.Default)),
                filePath: null));
            solution = solution.AddProject(ProjectInfo.Create(ids[g.Key], VersionStamp.Default, name, name, LanguageNames.CSharp,
                filePath: File.Exists(projFull) ? projFull : null,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable),
                parseOptions: parse, documents: docs, projectReferences: projectRefs, metadataReferences: refs));
        }
        src.Solution = solution;
        src.IndexDocuments();
        src.LoadMs = sw.Elapsed.TotalMilliseconds;
        return src;
    }

    static bool IsManagedAssembly(string path)
    {
        try { System.Reflection.AssemblyName.GetAssemblyName(path); return true; }
        catch (BadImageFormatException) { return false; }
        catch (FileLoadException) { return false; }
    }

    /// <summary>Shared frameworks next to the running runtime: Microsoft.NETCore.App and, when installed, Microsoft.AspNetCore.App.</summary>
    public static IEnumerable<string> DefaultReferenceDirectories()
    {
        var core = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        yield return core;
        var version = Path.GetFileName(core);
        var asp = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(core)!)!, "Microsoft.AspNetCore.App", version);
        if (Directory.Exists(asp)) yield return asp;
    }

    public override Dictionary<string, object?> Describe()
    {
        var d = base.Describe();
        d["reference_directories"] = ReferenceDirectories;
        return d;
    }

    public override void Dispose() => _workspace.Dispose();
}
