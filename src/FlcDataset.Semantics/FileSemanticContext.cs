using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FlcDataset.Semantics;

/// <summary>
/// Per-file state shared by every caret and policy of one document: original text/tree/semantic model (bound lazily and
/// cached by Roslyn inside the compilation) and an index of identifier token positions used for prefix ranking and the
/// leakage audit.
/// </summary>
public sealed class FileSemanticContext
{
    public required Document Document { get; init; }
    public required string Text { get; init; }
    public required SyntaxTree Tree { get; init; }
    public required SyntaxNode Root { get; init; }
    public required SemanticModel Model { get; init; }
    Dictionary<string, int[]> _identifierPositions = null!;

    public static async Task<FileSemanticContext> CreateAsync(Document document, CancellationToken ct)
    {
        var tree = await document.GetSyntaxTreeAsync(ct) ?? throw new InvalidOperationException("no_syntax_tree");
        var root = await tree.GetRootAsync(ct);
        var model = await document.GetSemanticModelAsync(ct) ?? throw new InvalidOperationException("no_semantic_model");
        var positions = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var t in root.DescendantTokens(descendIntoTrivia: false))
            if (t.IsKind(SyntaxKind.IdentifierToken))
                (positions.TryGetValue(t.ValueText, out var l) ? l : positions[t.ValueText] = []).Add(t.SpanStart);
        return new FileSemanticContext
        {
            Document = document, Text = (await tree.GetTextAsync(ct)).ToString(), Tree = tree, Root = root, Model = model,
            _identifierPositions = positions.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal),
        };
    }

    readonly ConcurrentDictionary<(int Pos, ISymbol Container, bool Ext), System.Collections.Immutable.ImmutableArray<ISymbol>> _lookups = new();
    readonly ConcurrentDictionary<(ISymbol Symbol, int Pos, SymbolDisplayFormat Format), string> _display = new();
    readonly ConcurrentDictionary<int, (int Pos, INamedTypeSymbol Type)?> _typeAnchors = new();

    /// <summary>Members of a container visible at a canonical position of the original model (cached per file).</summary>
    public System.Collections.Immutable.ImmutableArray<ISymbol> LookupMembers(int pos, INamespaceOrTypeSymbol container, bool extensions) =>
        _lookups.GetOrAdd((pos, container, extensions), k => Model.LookupSymbols(k.Pos, (INamespaceOrTypeSymbol)k.Container, includeReducedExtensionMethods: k.Ext));

    /// <summary>Minimal display string at a canonical position (deterministic: independent of which caret computes it first).</summary>
    public string Display(ISymbol symbol, int pos, SymbolDisplayFormat format) =>
        _display.GetOrAdd((symbol, pos, format), k => k.Symbol.ToMinimalDisplayString(Model, k.Pos, k.Format));

    /// <summary>
    /// Canonical position for member lookups/display inside the innermost type declaration (of this file) that contains the
    /// caret in its body: just after the type's open brace. Text up to there is identical in every snapshot of this file.
    /// </summary>
    public (int Pos, INamedTypeSymbol Type)? TypeAnchor(int caret) => _typeAnchors.GetOrAdd(caret, c =>
    {
        foreach (var n in Root.FindToken(c).Parent?.AncestorsAndSelf() ?? [])
            if (n is BaseTypeDeclarationSyntax t && !t.OpenBraceToken.IsMissing && t.OpenBraceToken.Span.End <= c)
                return Model.GetDeclaredSymbol(t) is INamedTypeSymbol sym ? (t.OpenBraceToken.Span.End, sym) : null;
        return null;
    });

    /// <summary>Distinct identifier names with an occurrence in [start, end), ordinal order.</summary>
    public IEnumerable<string> IdentifiersIn(int start, int end) =>
        _identifierPositions.Keys.Where(n => OccursIn(n, start, end)).Order(StringComparer.Ordinal);

    /// <summary>True when an identifier token with this name starts in [start, end).</summary>
    public bool OccursIn(string name, int start, int end)
    {
        if (!_identifierPositions.TryGetValue(name, out var p)) return false;
        int i = Array.BinarySearch(p, start);
        if (i < 0) i = ~i;
        return i < p.Length && p[i] < end;
    }

    /// <summary>True when an identifier token with this name starts outside [start, end).</summary>
    public bool OccursOutside(string name, int start, int end) =>
        _identifierPositions.TryGetValue(name, out var p) && (p[0] < start || p[^1] >= end);
}

/// <summary>Opt-in phase profiler for semantic analysis (process-wide; enabled by the semantic-profile command).</summary>
public static class SemanticProfile
{
    public static bool Enabled { get; set; }
    static readonly ConcurrentDictionary<string, long[]> Phases = new();

    public static Scope Measure(string phase) => new(Enabled ? phase : null);

    public static void Count(string key)
    {
        if (!Enabled) return;
        Interlocked.Increment(ref Phases.GetOrAdd("count." + key, _ => new long[2])[1]);
    }

    public readonly struct Scope(string? phase) : IDisposable
    {
        long _start { get; init; } = phase is null ? 0 : Stopwatch.GetTimestamp();
        public Scope WithStart(long start) => this with { _start = start };
        public void Dispose()
        {
            if (phase is null) return;
            var acc = Phases.GetOrAdd(phase, _ => new long[2]);
            Interlocked.Add(ref acc[0], Stopwatch.GetTimestamp() - _start);
            Interlocked.Increment(ref acc[1]);
        }
    }

    public static Dictionary<string, object> Snapshot() => Phases.OrderBy(k => k.Key).ToDictionary(k => k.Key, k => (object)new
    {
        total_ms = Math.Round(k.Value[0] * 1000.0 / Stopwatch.Frequency, 1),
        count = k.Value[1],
        mean_ms = k.Value[1] == 0 ? 0 : Math.Round(k.Value[0] * 1000.0 / Stopwatch.Frequency / k.Value[1], 3),
    });

    public static void Reset() => Phases.Clear();
}
