using System.Text;
using FlcDataset.Core;
using FlcDataset.Extraction;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace FlcDataset.Semantics;

/// <summary>
/// Computes semantic facts at a caret on an editor-equivalent snapshot in which the hidden target has been removed.
/// The original (complete) document is never bound; all facts come from the snapshot only.
/// </summary>
public static class SemanticAnalyzer
{
    static readonly SymbolDisplayFormat TypeFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                              SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier |
                              SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    static readonly SymbolDisplayFormat SignatureFormat = TypeFormat
        .WithMemberOptions(SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeRef)
        .WithParameterOptions(SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName |
                              SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeExtensionThis);

    static readonly SymbolDisplayFormat EnclosingFormat = SignatureFormat
        .WithMemberOptions(SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeContainingType)
        .WithParameterOptions(SymbolDisplayParameterOptions.IncludeType);

    static readonly HashSet<string> ObjectMembers = ["Equals", "GetHashCode", "GetType", "ToString", "ReferenceEquals", "MemberwiseClone", "Finalize"];

    /// <summary>Build the analyzed text for a policy. editor_snapshot keeps everything except the target; strict_prefix keeps only the prefix.</summary>
    public static string SnapshotText(string original, int caret, int targetEnd, string policy) => policy switch
    {
        VisibilityPolicy.EditorSnapshot => string.Concat(original.AsSpan(0, caret), original.AsSpan(targetEnd)),
        VisibilityPolicy.StrictPrefix => original[..caret] + BraceClosure(original[..caret]),
        _ => throw new ArgumentException("Unknown visibility policy " + policy),
    };

    /// <summary>Convenience overload (tests, one-off use): builds the per-file context on every call.</summary>
    public static async Task<SemanticRecord> AnalyzeAsync(Document original, FlcSampleRecord sample, string policy, SemanticConfig cfg, CancellationToken ct) =>
        await AnalyzeAsync(await FileSemanticContext.CreateAsync(original, ct), sample, policy, cfg, ct);

    /// <summary>How the target-free snapshot was bound.</summary>
    sealed record Binding(SemanticModel Model, SyntaxNode Root, SyntaxTree SnapshotTree, string Engine, int ParseErrors, string? SyntheticSuffix,
        SemanticModel? ArtifactCheckModel, bool FilterLaterDeclarations, int MinPosition = 0)
    {
        /// <summary>Nodes whose declarations may be in scope at the caret (enclosing member, or top-level statements).</summary>
        public IReadOnlyList<SyntaxNode> ScopeNodes { get; init; } = [];
        /// <summary>Implicit names (value in setters, args in top-level programs).</summary>
        public IReadOnlyList<string>? ImplicitNames { get; init; }
        /// <summary>Where a local's declaration may come from (snapshot tree by default).</summary>
        public Func<SyntaxReference, bool> LocalAllowed { get; init; } = r => r.SyntaxTree == SnapshotTree;
        /// <summary>speculative_scope: position in the original model where lookups run (type/namespace anchor before the caret).</summary>
        public int? QueryPosition { get; init; }
        /// <summary>speculative_scope: expressions come from the snapshot tree and are bound with GetSpeculative* at QueryPosition.</summary>
        public bool ScopeOnly { get; init; }
        public ISymbol? FixedEnclosing { get; init; }
        /// <summary>Drop symbols whose original declaration overlaps the edited span (their text is partly hidden).</summary>
        public bool HideEditedDeclarations { get; init; }
    }

    public static async Task<SemanticRecord> AnalyzeAsync(FileSemanticContext ctx, FlcSampleRecord sample, string policy, SemanticConfig cfg, CancellationToken ct)
    {
        var bindStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var spec = cfg.Engine != "fork" ? TrySpeculative(ctx, sample, policy, ct) : null;
        if (spec is not null)
        {
            if (SemanticProfile.Enabled) using (new SemanticProfile.Scope("bind.speculative").WithStart(bindStart)) { }
            try
            {
                return Analyze(ctx, spec, sample, policy, cfg, ct);
            }
            catch (Exception e) when (e is ArgumentException or NullReferenceException or InvalidOperationException && e is not OperationCanceledException)
            {
                // Speculative models reject some edge positions (e.g. caret at the very end of the speculated node) and Roslyn
                // has rare internal failures on broken members; the fork engine is the reference implementation.
                SemanticProfile.Count("speculative_exception_refork");
                bindStart = System.Diagnostics.Stopwatch.GetTimestamp();
            }
        }
        var fork = await Fork(ctx, sample, policy, ct);
        if (SemanticProfile.Enabled) using (new SemanticProfile.Scope("bind.fork").WithStart(bindStart)) { }
        return Analyze(ctx, fork, sample, policy, cfg, ct) with { AnalysisEngine = spec is null ? "fork" : "fork_after_speculative_error" };
    }

    static SemanticRecord Analyze(FileSemanticContext ctx, Binding b, FlcSampleRecord sample, string policy, SemanticConfig cfg, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var model = b.Model;
        var root = b.Root;
        int pos = sample.CaretUtf16Offset;
        int te = sample.TargetEndUtf16Offset;
        // Minimal type names are resolved at the line start (outside any half-typed expression) so that names bind normally.
        int qpos = b.QueryPosition ?? pos;
        int displayPos = b.ScopeOnly ? qpos : Math.Max(Math.Min(sample.LineStartUtf16Offset, pos), b.MinPosition);
        int shift = te - pos;

        bool IsSnapshot(SyntaxTree? t) => t == b.SnapshotTree;
        bool IsThisDocument(SyntaxTree? t) => t == b.SnapshotTree || t == ctx.Tree;
        // strict_prefix on a speculative member: declarations of this file after the caret do not exist in the prefix.
        bool LaterInDoc(ISymbol s) => b.FilterLaterDeclarations && s.DeclaringSyntaxReferences.Length > 0
            && s.DeclaringSyntaxReferences.All(r => r.SyntaxTree == ctx.Tree && r.Span.Start >= pos);
        // A declaration that contains the caret is still being typed (e.g. field `_` while typing `_maxLines`); it is not a fact yet.
        bool BeingTyped(ISymbol s) => s.DeclaringSyntaxReferences.Any(r => IsSnapshot(r.SyntaxTree)
            && DeclaredIdentifier(r.GetSyntax(ct)) is { RawKind: not 0 } id && id.SpanStart < pos && pos <= id.Span.End);
        // Only the declaration header (name/signature/type) matters: a member whose *body* contains the edit is fully typed.
        bool EditedDeclaration(ISymbol s) => b.HideEditedDeclarations
            && s.DeclaringSyntaxReferences.Any(r => r.SyntaxTree == ctx.Tree && HeaderSpan(r.GetSyntax(ct)) is var h && h.Start <= te && pos <= h.End);
        bool Artifact(ISymbol s) => LaterInDoc(s) || BeingTyped(s) || EditedDeclaration(s)
            || b.Engine == "fork" && IsRecoveryArtifact(s, b.SnapshotTree, pos, shift, b.ArtifactCheckModel, ct);
        SymbolInfo SymInfo(ExpressionSyntax e) => b.ScopeOnly
            ? model.GetSpeculativeSymbolInfo(qpos, e, e is NameSyntax ? SpeculativeBindingOption.BindAsTypeOrNamespace : SpeculativeBindingOption.BindAsExpression)
            : model.GetSymbolInfo(e, ct);
        ITypeSymbol? TypeOf(ExpressionSyntax e) => b.ScopeOnly
            ? model.GetSpeculativeTypeInfo(qpos, e, SpeculativeBindingOption.BindAsExpression).Type
            : model.GetTypeInfo(e, ct).Type;

        ISymbol? enclosing, member;
        INamedTypeSymbol? containingType;
        string Min(ITypeSymbol? t) => t is null ? "?" : t.ToMinimalDisplayString(model, displayPos, TypeFormat);
        string Sig(ISymbol s) => s.ToMinimalDisplayString(model, displayPos, SignatureFormat);
        // Member facts (this/receiver) use per-file caches at the canonical type anchor when the caret is inside a type body.
        // Fork analyzes its own compilation: no cross-snapshot caches (and no original declarations).
        var anchor = b.Engine.StartsWith("fork", StringComparison.Ordinal) ? null : ctx.TypeAnchor(pos);
        string CMin(ITypeSymbol? t) => t is null ? "?" : anchor is { } a ? ctx.Display(t, a.Pos, TypeFormat) : Min(t);
        string CSig(ISymbol s) => anchor is { } a ? ctx.Display(s, a.Pos, SignatureFormat) : Sig(s);
        IEnumerable<ISymbol> Members(INamespaceOrTypeSymbol container, bool ext) =>
            anchor is { } a ? ctx.LookupMembers(a.Pos, container, ext) : model.LookupSymbols(qpos, container, includeReducedExtensionMethods: ext);
        // Names already typed in the visible prefix rank first when lists must be capped (like IDE "recently used" ranking).
        bool InPrefix(string name) => ctx.OccursIn(name.TrimStart('@'), Math.Max(0, pos - 20000), pos);

        var declaredHere = new HashSet<string>(StringComparer.Ordinal);
        // Seeds for the TYPE block: types of facts already derived from the snapshot/prefix (never from the target).
        var typeSeeds = new List<(ITypeSymbol Type, string Source)>();
        int artifacts = 0;
        var locals = new List<SymbolFact>();
        var parameters = new List<SymbolFact>();
        var tokenBefore = pos > 0 ? root.FindToken(pos - 1) : default;
        bool staticContext;
        List<SymbolFact> thisList;
        bool truncated;

        using (SemanticProfile.Measure("scope." + b.Engine))
        {
            enclosing = b.ScopeOnly ? b.FixedEnclosing : model.GetEnclosingSymbol(pos, ct);
            member = enclosing;
            while (member is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction } m && m.ContainingSymbol is not null)
                member = m.ContainingSymbol;
            containingType = enclosing as INamedTypeSymbol ?? enclosing?.ContainingType;
            staticContext = member?.IsStatic == true;
            // A declarator that contains the caret is still being typed; its symbol is not usable yet.
            var currentDeclarator = tokenBefore.Parent?.FirstAncestorOrSelf<VariableDeclaratorSyntax>();

            // Locals/parameters: Roslyn resolves, at the caret, each name declared in the enclosing scope nodes. This replaces a
            // global LookupSymbols(pos), which enumerates every imported type and namespace, with a few hashed lookups.
            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var n in b.ScopeNodes) CollectDeclaredNames(n, names, pos);
            if (containingType is not null)
                foreach (var r in containingType.DeclaringSyntaxReferences)
                    if (r.GetSyntax(ct) is TypeDeclarationSyntax { ParameterList: { } pl })
                        foreach (var p in pl.Parameters) names.Add(p.Identifier.ValueText);
            if (b.ImplicitNames is { } implicitNames) foreach (var n in implicitNames) names.Add(n);

            var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var shadowing = new HashSet<string>(StringComparer.Ordinal);
            using (SemanticProfile.Measure("scope.lookup." + b.Engine))
            {
                foreach (var name in names)
                foreach (var s in model.LookupSymbols(qpos, name: name))
                {
                    if (!seen.Add(s)) continue;
                    switch (s)
                    {
                        case ILocalSymbol l:
                        {
                            var decl = l.DeclaringSyntaxReferences.FirstOrDefault();
                            if (decl is null || !b.LocalAllowed(decl)) continue;
                            if (decl.Span.Start >= pos) continue; // declared later in the block: in scope but not yet usable
                            if (currentDeclarator is not null && decl.Span == currentDeclarator.Span) continue;
                            if (BeingTyped(l)) continue; // e.g. `out var su|bscriptions`: the name is still being typed
                            // `X or` + removed line parses as a declaration pattern whose designation is the combinator keyword.
                            if (l.Name is "or" or "and" or "not") continue;
                            shadowing.Add(l.Name);
                            // Locals of earlier top-level statements live in the original tree (text before the caret is identical).
                            var (lt, ann) = LocalType(decl.SyntaxTree == ctx.Tree && decl.SyntaxTree != b.SnapshotTree ? ctx.Model : model, l, decl.GetSyntax(ct), ct);
                            locals.Add(new SymbolFact { Name = Esc(l.Name), Kind = l.IsConst ? "const" : "local", Type = Min(lt), NullableAnnotation = ann });
                            typeSeeds.Add((lt, "local"));
                            declaredHere.Add(l.Name);
                            break;
                        }
                        case IRangeVariableSymbol r:
                            if (r.DeclaringSyntaxReferences.FirstOrDefault() is { } rd && rd.Span.Start >= pos) continue;
                            if (BeingTyped(r)) continue;
                            shadowing.Add(r.Name);
                            locals.Add(new SymbolFact { Name = Esc(r.Name), Kind = "range_variable" });
                            declaredHere.Add(r.Name);
                            break;
                        case IParameterSymbol p:
                        {
                            bool primary = p.ContainingSymbol is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor
                                           && ctor.DeclaringSyntaxReferences.Any(r => r.GetSyntax(ct) is TypeDeclarationSyntax);
                            if (primary && staticContext) continue;
                            if (p.DeclaringSyntaxReferences.FirstOrDefault() is { } pd && IsSnapshot(pd.SyntaxTree) && pd.Span.Start >= pos) continue;
                            if (BeingTyped(p)) continue;
                            shadowing.Add(p.Name);
                            parameters.Add(new SymbolFact
                            {
                                Name = Esc(p.Name), Kind = primary ? "primary_ctor_parameter" : p.ContainingSymbol is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction } ? "lambda_parameter" : "parameter",
                                Type = Min(p.Type), NullableAnnotation = Ann(p.Type),
                            });
                            if (p.DeclaringSyntaxReferences.Any(r => IsThisDocument(r.SyntaxTree))) declaredHere.Add(p.Name);
                            typeSeeds.Add((p.Type, "parameter"));
                            break;
                        }
                    }
                }
            }

            // Members of this type, its bases and enclosing types; display strings are built only for the kept top-N groups.
            var groups = new Dictionary<string, (List<ISymbol> Symbols, int Rank)>(StringComparer.Ordinal);
            for (var t = containingType; t is not null; t = t.ContainingType)
            {
                foreach (var s in Members(t, false))
                {
                    if (s is not (IFieldSymbol or IPropertySymbol or IMethodSymbol or IEventSymbol)) continue;
                    if (s.IsImplicitlyDeclared || s.ContainingType is null || s.ContainingType.SpecialType == SpecialType.System_Object) continue;
                    if (s is IMethodSymbol { MethodKind: not MethodKind.Ordinary }) continue;
                    if (staticContext && !s.IsStatic) continue;
                    if (!ReferenceEquals(t, containingType) && !s.IsStatic) continue; // outer types: only statics are usable
                    if (shadowing.Contains(s.Name)) continue;                          // hidden by a local/parameter
                    if (Artifact(s)) { artifacts++; continue; }
                    int rank = SymbolEqualityComparer.Default.Equals(s.ContainingType, containingType) ? 0 : 1;
                    if (groups.TryGetValue(s.Name, out var g)) { g.Symbols.Add(s); groups[s.Name] = (g.Symbols, Math.Min(g.Rank, rank)); }
                    else groups[s.Name] = ([s], rank);
                }
            }
            thisList = groups.OrderBy(x => InPrefix(x.Key) ? 0 : 1).ThenBy(x => x.Value.Rank).ThenBy(x => KindOrder(KindOf(x.Value.Symbols[0])))
                .ThenBy(x => x.Key, StringComparer.Ordinal).Take(cfg.MaxThisMembers)
                .Select(x => GroupFact(x.Value.Symbols, CMin, CSig)).ToList();
            foreach (var (name, g) in groups)
                if (g.Symbols.Any(s => s.DeclaringSyntaxReferences.Any(r => IsThisDocument(r.SyntaxTree)))) declaredHere.Add(name);
            foreach (var g in groups.Values)
                foreach (var s in g.Symbols)
                    if (s is IFieldSymbol f) typeSeeds.Add((f.Type, "member"));
                    else if (s is IPropertySymbol pr) typeSeeds.Add((pr.Type, "member"));
            truncated = locals.Count >= cfg.MaxScopeSymbols || groups.Count > cfg.MaxThisMembers;
        }

        locals = locals.OrderBy(x => x.Name, StringComparer.Ordinal).Take(cfg.MaxScopeSymbols).ToList();
        parameters = parameters.OrderBy(x => x.Kind == "primary_ctor_parameter" ? 1 : 0).ThenBy(x => x.Name, StringComparer.Ordinal).Take(cfg.MaxScopeSymbols).ToList();

        ct.ThrowIfCancellationRequested();
        // Receiver after '.', '?.' or in a logging call.
        string? receiverType = null, receiverKind = null, reason = null;
        var members = new List<SymbolFact>();
        ExpressionSyntax? receiverExpr = null;
        using (SemanticProfile.Measure("receiver." + b.Engine))
        {
            if (tokenBefore.IsKind(SyntaxKind.DotToken) && tokenBefore.Span.End == pos)
            {
                receiverExpr = tokenBefore.Parent switch
                {
                    MemberAccessExpressionSyntax ma => ma.Expression,
                    MemberBindingExpressionSyntax mb => mb.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>()?.Expression,
                    QualifiedNameSyntax qn => qn.Left,
                    _ => null,
                };
            }
            if (receiverExpr is not null)
            {
                var symbol = SymInfo(receiverExpr).Symbol;
                INamespaceOrTypeSymbol? container = null;
                bool staticAccess = false;
                if (symbol is INamespaceSymbol ns) { container = ns; receiverKind = "namespace"; receiverType = ns.ToDisplayString(); }
                else if (symbol is INamedTypeSymbol nt && receiverExpr is not (InvocationExpressionSyntax or ElementAccessExpressionSyntax))
                {
                    if (!LaterInDoc(nt)) { container = nt; staticAccess = true; receiverKind = "type"; receiverType = Min(nt); }
                    else reason = "unresolved_receiver_type";
                }
                else
                {
                    var t = TypeOf(receiverExpr);
                    if (t is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable && tokenBefore.Parent is MemberBindingExpressionSyntax)
                        t = nullable.TypeArguments[0];
                    if (t is not null && t.TypeKind != TypeKind.Error && !LaterInDoc(t)) { container = t; receiverKind = "instance"; receiverType = Min(t); }
                    else reason = "unresolved_receiver_type";
                }
                if (container is not null)
                {
                    var groups = new Dictionary<string, (List<ISymbol> Symbols, int Rank, bool Ext)>(StringComparer.Ordinal);
                    foreach (var s in Members(container, receiverKind == "instance"))
                    {
                        if (s.IsImplicitlyDeclared && s is not INamespaceSymbol) continue;
                        if (receiverKind == "instance")
                        {
                            if (s.IsStatic && s is not IMethodSymbol { MethodKind: MethodKind.ReducedExtension }) continue;
                            if (s is INamedTypeSymbol) continue;
                        }
                        if (staticAccess && !s.IsStatic && s is not INamedTypeSymbol) continue;
                        if (s is IMethodSymbol { MethodKind: not (MethodKind.Ordinary or MethodKind.ReducedExtension) }) continue;
                        if (ObjectMembers.Contains(s.Name) && s.ContainingType?.SpecialType == SpecialType.System_Object) continue;
                        if (Artifact(s)) { artifacts++; continue; }
                        var isExt = s is IMethodSymbol { MethodKind: MethodKind.ReducedExtension } || s is IMethodSymbol { IsExtensionMethod: true };
                        int rank = isExt ? 2 : SymbolEqualityComparer.Default.Equals(s.ContainingSymbol, container) ? 0 : 1;
                        if (groups.TryGetValue(s.Name, out var g)) { g.Symbols.Add(s); groups[s.Name] = (g.Symbols, Math.Min(g.Rank, rank), g.Ext && isExt); }
                        else groups[s.Name] = ([s], rank, isExt);
                    }
                    truncated |= groups.Count > cfg.MaxMembers;
                    members = groups.OrderBy(x => InPrefix(x.Key) ? 0 : 1).ThenBy(x => x.Value.Rank).ThenBy(x => KindOrder(KindOf(x.Value.Symbols[0])))
                        .ThenBy(x => x.Key, StringComparer.Ordinal).Take(cfg.MaxMembers)
                        .Select(x => GroupFact(x.Value.Symbols, CMin, CSig) with { IsExtension = x.Value.Ext }).ToList();
                }
            }
        }

        ct.ThrowIfCancellationRequested();
        // Invocation context: '(' or ',' of an argument list.
        var candidates = new List<InvocationCandidate>();
        string? expectedType = null, expectedSource = null;
        using (SemanticProfile.Measure("invocation." + b.Engine))
        {
            if (!b.ScopeOnly && tokenBefore.Parent is ArgumentListSyntax argList && (tokenBefore.IsKind(SyntaxKind.OpenParenToken) || tokenBefore.IsKind(SyntaxKind.CommaToken)))
            {
                int argIndex = argList.Arguments.GetSeparators().Count(sep => sep.Span.End <= pos);
                var callee = argList.Parent;
                IEnumerable<ISymbol> group = callee switch
                {
                    InvocationExpressionSyntax inv => model.GetMemberGroup(inv.Expression, ct),
                    BaseObjectCreationExpressionSyntax oc => model.GetMemberGroup(oc, ct) is { Length: > 0 } mg ? mg : CandidatesOf(model.GetSymbolInfo(oc, ct)),
                    _ => [],
                };
                foreach (var m in group.OfType<IMethodSymbol>().Where(m => !LaterInDoc(m)).OrderBy(Sig, StringComparer.Ordinal))
                {
                    var prm = argIndex < m.Parameters.Length ? m.Parameters[argIndex]
                        : m.Parameters.Length > 0 && m.Parameters[^1].IsParams ? m.Parameters[^1] : null;
                    if (prm is null && argIndex > 0) continue; // inapplicable arity
                    candidates.Add(new InvocationCandidate { Signature = Sig(m), ArgumentIndex = argIndex, ParameterName = prm?.Name, ParameterType = prm is null ? null : Min(prm.Type) });
                    if (candidates.Count >= cfg.MaxMembers) { truncated = true; break; }
                }
                var distinct = candidates.Where(c => c.ParameterType is not null).Select(c => c.ParameterType).Distinct().ToList();
                if (distinct.Count == 1) { expectedType = distinct[0]; expectedSource = "argument"; }
                if (candidates.Count == 0) reason ??= "no_invocation_candidates";
            }

            // Other expected-type contexts. Only emitted when the type is bound (not an error type).
            if (expectedType is null && !b.ScopeOnly)
                (expectedType, expectedSource) = ExpectedType(model, root, tokenBefore, pos, member, enclosing, Min, ct);

            // Logging call: receiver type of the enclosing invocation (confirms ILogger usage semantically).
            if (sample.CaretKind == "log_message" && receiverType is null && !b.ScopeOnly)
            {
                var inv = tokenBefore.Parent?.FirstAncestorOrSelf<InvocationExpressionSyntax>();
                if (inv?.Expression is MemberAccessExpressionSyntax lma && model.GetTypeInfo(lma.Expression, ct).Type is { TypeKind: not TypeKind.Error } lt)
                { receiverType = Min(lt); receiverKind = "logger"; }
            }
        }

        ct.ThrowIfCancellationRequested();
        List<TypeContract> contextTypes;
        using (SemanticProfile.Measure("types." + b.Engine))
            contextTypes = ContextTypes();

        // TYPE block: contracts of nearby project types. Candidates come only from facts above (in-scope symbol types), base types
        // of the enclosing type and type names already typed in the visible prefix; the receiver/enclosing types are excluded
        // because MEMBER/THIS already cover them. Every accessible member is eligible (capped), not just the one the target uses.
        List<TypeContract> ContextTypes()
        {
            if (cfg.MaxContextTypes <= 0) return [];
            if (containingType is not null)
            {
                if (containingType.BaseType is { } bt) typeSeeds.Add((bt, "base"));
                foreach (var i in containingType.Interfaces) typeSeeds.Add((i, "base"));
            }
            // Type names in the visible prefix of the current member (bounded window), resolved at the caret's scope.
            // Window = the enclosing member's text before the caret (from the original tree, identical before the caret for every engine).
            var memberNode = ctx.Root.FindToken(Math.Max(0, pos - 1)).Parent?.AncestorsAndSelf()
                .LastOrDefault(n => n is MemberDeclarationSyntax and not (BaseTypeDeclarationSyntax or BaseNamespaceDeclarationSyntax) || n is GlobalStatementSyntax);
            int windowStart = Math.Max(Math.Max(0, pos - 3000), memberNode?.SpanStart ?? pos);
            foreach (var name in ctx.IdentifiersIn(windowStart, pos))
            {
                if (name.Length < 2 || !char.IsUpper(name[0])) continue;
                foreach (var t in model.LookupNamespacesAndTypes(displayPos, name: name).OfType<INamedTypeSymbol>())
                    typeSeeds.Add((t, "prefix"));
            }

            int SourceRank(string s) => s switch { "local" or "parameter" => 0, "member" => 1, "prefix" => 2, _ => 3 };
            var picked = new Dictionary<INamedTypeSymbol, string>(SymbolEqualityComparer.Default);
            foreach (var (seed, source) in typeSeeds)
                foreach (var t in Expand(seed))
                {
                    if (!Eligible(t)) continue;
                    if (!picked.TryGetValue(t, out var existing) || SourceRank(source) < SourceRank(existing)) picked[t] = source;
                }

            bool Eligible(INamedTypeSymbol t)
            {
                if (t.TypeKind is not (TypeKind.Class or TypeKind.Interface or TypeKind.Struct or TypeKind.Enum)) return false;
                if (t.SpecialType != SpecialType.None || t.IsImplicitlyDeclared || t.IsAnonymousType) return false;
                if (!cfg.ContextTypesIncludeMetadata && !t.Locations.Any(l => l.IsInSource)) return false;
                if (containingType is not null && SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, containingType.OriginalDefinition)) return false;
                if (receiverType is not null && receiverKind is "instance" or "type" && Min(t) == receiverType) return false;
                return !LaterInDoc(t) && !EditedDeclaration(t);
            }

            var result = new List<TypeContract>();
            foreach (var (t, source) in picked.OrderBy(x => SourceRank(x.Value)).ThenBy(x => InPrefix(x.Key.Name) ? 0 : 1)
                         .ThenBy(x => x.Key.Name, StringComparer.Ordinal).ThenBy(x => Min(x.Key), StringComparer.Ordinal).Take(cfg.MaxContextTypes))
            {
                var groups = new Dictionary<string, List<ISymbol>>(StringComparer.Ordinal);
                foreach (var s in Members(t, false))
                {
                    if (s.IsImplicitlyDeclared || s is INamedTypeSymbol) continue;
                    if (s is not (IFieldSymbol or IPropertySymbol or IMethodSymbol or IEventSymbol)) continue;
                    if (s is IMethodSymbol { MethodKind: not MethodKind.Ordinary }) continue;
                    if (ObjectMembers.Contains(s.Name) && s.ContainingType?.SpecialType == SpecialType.System_Object) continue;
                    if (Artifact(s)) continue;
                    (groups.TryGetValue(s.Name, out var l) ? l : groups[s.Name] = []).Add(s);
                }
                var ctors = t.TypeKind is TypeKind.Class or TypeKind.Struct && !t.IsAbstract && !t.IsStatic
                    ? t.InstanceConstructors.Where(c => !c.IsImplicitlyDeclared && model.IsAccessible(displayPos, c) && !Artifact(c)).ToList() : [];
                var facts = new List<SymbolFact>();
                if (ctors.Count > 0)
                {
                    var rep = ctors.OrderBy(c => c.Parameters.Length).ThenBy(CSig, StringComparer.Ordinal).First();
                    facts.Add(new SymbolFact { Name = "new", Kind = "constructor", Signature = CtorSig(rep), Overloads = ctors.Count });
                }
                // Project-declared members before ones inherited from libraries (e.g. DbContext's ChangeTracker).
                static int Origin(List<ISymbol> g) => g.Any(m => m.Locations.Any(l => l.IsInSource)) ? 0 : 1;
                facts.AddRange(groups.OrderBy(x => InPrefix(x.Key) ? 0 : 1).ThenBy(x => Origin(x.Value)).ThenBy(x => KindOrder(KindOf(x.Value[0]))).ThenBy(x => x.Key, StringComparer.Ordinal)
                    .Take(Math.Max(0, cfg.MaxTypeMembers - facts.Count)).Select(x => GroupFact(x.Value, CMin, CSig)));
                result.Add(new TypeContract
                {
                    Name = CMin(t), Kind = t.IsRecord ? "record" : t.TypeKind.ToString().ToLowerInvariant(), Source = source, IsStatic = t.IsStatic,
                    Members = facts, TotalMembers = groups.Count + (ctors.Count > 0 ? 1 : 0),
                });
            }
            return result;

            string CtorSig(IMethodSymbol c) => "new(" + string.Join(", ", c.Parameters.Select(p => CMin(p.Type) + " " + Esc(p.Name))) + ")";
        }

        static IEnumerable<INamedTypeSymbol> Expand(ITypeSymbol t)
        {
            switch (t)
            {
                case IArrayTypeSymbol a:
                    foreach (var x in Expand(a.ElementType)) yield return x;
                    break;
                case INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n:
                    foreach (var x in Expand(n.TypeArguments[0])) yield return x;
                    break;
                case INamedTypeSymbol n:
                    yield return n;
                    // Generic arguments (Task<Order>, List<OrderItem>, IRepository<Order>) usually carry the project types.
                    foreach (var arg in n.TypeArguments)
                        if (arg is INamedTypeSymbol na && !na.IsGenericType) yield return na;
                        else if (arg is IArrayTypeSymbol { ElementType: INamedTypeSymbol ae }) yield return ae;
                    break;
            }
        }

        string status;
        if (enclosing is null) { status = SemanticStatus.PartiallyResolved; reason ??= "no_enclosing_symbol"; }
        else if (receiverExpr is not null && receiverType is null) status = SemanticStatus.PartiallyResolved;
        else if (reason is "no_invocation_candidates") status = SemanticStatus.PartiallyResolved;
        else status = SemanticStatus.Resolved;

        using (SemanticProfile.Measure("render_audit." + b.Engine))
        {
            var record = new SemanticRecord
            {
                SampleId = sample.SampleId,
                VisibilityPolicy = policy,
                AnalysisEngine = b.Engine,
                Status = status,
                Reason = reason,
                Project = ctx.Document.Project.Name,
                EnclosingSymbol = member?.ToDisplayString(EnclosingFormat),
                EnclosingKind = enclosing is null ? null : KindName(enclosing),
                EnclosingType = containingType is null ? null : Min(containingType),
                ReturnType = member is IMethodSymbol mm ? Min(mm.ReturnType) : member is IPropertySymbol pp ? Min(pp.Type) : null,
                ExpectedType = expectedType,
                ExpectedTypeSource = expectedSource,
                Locals = locals,
                Parameters = parameters,
                ThisMembers = thisList,
                ReceiverType = receiverType,
                ReceiverKind = receiverKind,
                Members = members,
                InvocationCandidates = candidates,
                ContextTypes = contextTypes,
                SnapshotSyntaxErrors = b.ParseErrors,
                SyntheticSuffix = b.SyntheticSuffix,
                Truncated = truncated,
                DroppedRecoveryArtifacts = artifacts,
            };
            record = record with { Prompt = RenderPrompt(record) };
            var audit = Audit(record, sample, ctx, declaredHere);
            if (audit.Violations.Count == 0) return record with { Leakage = audit };
            // A fact that cannot be traced outside the hidden target is never shipped: the semantic payload of this sample is
            // dropped (status failed, reason names the symbols) while the syntax sample itself stays valid.
            return new SemanticRecord
            {
                SampleId = sample.SampleId, VisibilityPolicy = policy, AnalysisEngine = b.Engine, Status = SemanticStatus.Failed,
                Reason = "leak_audit:" + string.Join(",", audit.Violations), Project = ctx.Document.Project.Name,
                SnapshotSyntaxErrors = b.ParseErrors, SyntheticSuffix = b.SyntheticSuffix, DroppedRecoveryArtifacts = artifacts,
                Leakage = audit with { Violations = [] },
            };
        }
    }

    /// <summary>Full-document fork: the snapshot text replaces the document in a new solution snapshot (new compilation).</summary>
    static async Task<Binding> Fork(FileSemanticContext ctx, FlcSampleRecord s, string policy, CancellationToken ct)
    {
        var snapshot = SnapshotText(ctx.Text, s.CaretUtf16Offset, s.TargetEndUtf16Offset, policy);
        var doc = ctx.Document.WithText(SourceText.From(snapshot));
        var model = await doc.GetSemanticModelAsync(ct) ?? throw new InvalidOperationException("no_semantic_model");
        var root = await model.SyntaxTree.GetRootAsync(ct);
        var errors = model.SyntaxTree.GetDiagnostics(ct).Count(d => d.Severity == DiagnosticSeverity.Error);
        var (scopeNodes, implicitNames) = ForkScope(root, s.CaretUtf16Offset);
        return new Binding(model, root, model.SyntaxTree, "fork", errors,
            policy == VisibilityPolicy.StrictPrefix ? snapshot[s.CaretUtf16Offset..] : null,
            policy == VisibilityPolicy.EditorSnapshot ? ctx.Model : null, FilterLaterDeclarations: false)
        { ScopeNodes = scopeNodes, ImplicitNames = implicitNames };
    }

    /// <summary>Scope nodes in a forked snapshot: the enclosing member, or all top-level statements.</summary>
    static (IReadOnlyList<SyntaxNode>, IReadOnlyList<string>?) ForkScope(SyntaxNode root, int pos)
    {
        SyntaxNode? memberNode = null;
        string? implicitName = null;
        bool insideType = false;
        // The token before the caret identifies the scope being typed in; the token at the caret covers line starts.
        var tokens = pos > 0 ? new[] { root.FindToken(pos - 1), root.FindToken(pos) } : [root.FindToken(pos)];
        foreach (var tok in tokens)
        {
            foreach (var n in tok.Parent?.AncestorsAndSelf() ?? [])
            {
                if (n is AccessorDeclarationSyntax acc && acc.Keyword.Kind() is SyntaxKind.SetKeyword or SyntaxKind.InitKeyword or SyntaxKind.AddKeyword or SyntaxKind.RemoveKeyword)
                    implicitName ??= "value";
                if (n is GlobalStatementSyntax && root is CompilationUnitSyntax cu)
                    return (cu.Members.OfType<GlobalStatementSyntax>().ToList(), ["args"]);
                if (n is MemberDeclarationSyntax and not (BaseTypeDeclarationSyntax or BaseNamespaceDeclarationSyntax)) memberNode ??= n;
                if (n is BaseTypeDeclarationSyntax) { insideType = true; break; }
            }
            if (memberNode is not null) break;
        }
        if (memberNode is null && !insideType && root is CompilationUnitSyntax top && top.Members.OfType<GlobalStatementSyntax>().Any())
            return (top.Members.OfType<GlobalStatementSyntax>().ToList(), ["args"]);
        return (memberNode is null ? [] : [memberNode], implicitName is null ? null : [implicitName]);
    }

    /// <summary>
    /// Speculative binding of the enclosing method/accessor only: the member text with the target removed (editor_snapshot) or
    /// cut at the caret and brace-closed (strict_prefix) is re-parsed at its original offset and bound against the original,
    /// already-built compilation. No new compilation is created. Returns null when the caret is not inside a member body
    /// (field initializers, attributes, top-level statements, signatures), in which case the caller forks.
    /// </summary>
    static Binding? TrySpeculative(FileSemanticContext ctx, FlcSampleRecord s, string policy, CancellationToken ct)
    {
        int caret = s.CaretUtf16Offset, te = s.TargetEndUtf16Offset;
        var origHost = FindBodyHost(ctx.Root, caret, te, out var anchor, out var accessorIndex);
        if (origHost is not null && TryStatementStart(ctx, s, policy, origHost) is { } atStart) return atStart;
        if (origHost is null)
        {
            var other = TrySpeculativeOther(ctx, s, policy, ct) ?? TrySpeculativeScope(ctx, s, policy, ct);
            if (other is null && SemanticProfile.Enabled) SemanticProfile.Count("fallback." + FallbackReason(ctx.Root, caret));
            return other;
        }

        // Incremental re-parse of the snapshot (same text as the fork engine), so positions are absolute and match the original
        // before the caret. Only the enclosing member is then bound speculatively against the original compilation.
        var (newTree, newRoot, suffix) = SnapshotTree(ctx, caret, te, policy, ct);
        var newHost = FindBodyHost(newRoot, caret, caret, out _, out var newAccessorIndex);
        if (newHost is null || newHost.RawKind != origHost.RawKind || newHost.SpanStart != origHost.SpanStart || newAccessorIndex != accessorIndex)
            return null;
        if (!SameSignature(origHost, newHost)) return null;

        SemanticModel? spec = null;
        bool ok;
        try
        {
            ok = newHost switch
            {
                BaseMethodDeclarationSyntax m => ctx.Model.TryGetSpeculativeSemanticModelForMethodBody(anchor, m, out spec),
                AccessorDeclarationSyntax acc => ctx.Model.TryGetSpeculativeSemanticModelForMethodBody(anchor, acc, out spec),
                _ => false,
            };
        }
        catch (Exception e) when (e is NullReferenceException or ArgumentException or InvalidOperationException)
        {
            ok = false; // Roslyn may reject heavily broken members; the fork engine handles them.
        }
        if (!ok || spec is null) return null;
        var errors = newHost.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error);
        IReadOnlyList<string>? implicitNames = newHost is AccessorDeclarationSyntax a2
            && a2.Keyword.Kind() is SyntaxKind.SetKeyword or SyntaxKind.InitKeyword or SyntaxKind.AddKeyword or SyntaxKind.RemoveKeyword ? ["value"] : null;
        IReadOnlyList<SyntaxNode> scope = newHost is AccessorDeclarationSyntax { Parent.Parent: IndexerDeclarationSyntax ix } ? [newHost, ix.ParameterList] : [newHost];
        return new Binding(spec, newHost, newTree, "speculative", errors, suffix, null,
            FilterLaterDeclarations: policy == VisibilityPolicy.StrictPrefix, MinPosition: anchor)
        { ScopeNodes = scope, ImplicitNames = implicitNames };
    }

    static (SyntaxTree Tree, SyntaxNode Root, string? Suffix) SnapshotTree(FileSemanticContext ctx, int caret, int te, string policy, CancellationToken ct)
    {
        var oldText = ctx.Tree.GetText(ct);
        string? suffix = null;
        TextChange change;
        if (policy == VisibilityPolicy.EditorSnapshot) change = new TextChange(TextSpan.FromBounds(caret, te), "");
        else
        {
            suffix = BraceClosure(ctx.Text[..caret]);
            change = new TextChange(TextSpan.FromBounds(caret, ctx.Text.Length), suffix);
        }
        var tree = ctx.Tree.WithChangedText(oldText.WithChanges(change));
        return (tree, tree.GetRoot(ct), suffix);
    }

    /// <summary>
    /// Speculative binding outside member bodies: field/property initializers, expression-bodied properties, attributes and
    /// top-level statements. The snapshot node (same kind, same start) is bound against the original compilation.
    /// </summary>
    static Binding? TrySpeculativeOther(FileSemanticContext ctx, FlcSampleRecord s, string policy, CancellationToken ct)
    {
        int caret = s.CaretUtf16Offset, te = s.TargetEndUtf16Offset;
        SyntaxNode? orig = null;
        foreach (var n in ctx.Root.FindToken(caret).Parent?.AncestorsAndSelf() ?? [])
        {
            if (n is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax } or PropertyDeclarationSyntax } evc
                && evc.EqualsToken.Span.End <= caret && te <= evc.Parent!.FirstAncestorOrSelf<MemberDeclarationSyntax>()!.Span.End) { orig = evc; break; }
            if (n is ArrowExpressionClauseSyntax { Parent: PropertyDeclarationSyntax or IndexerDeclarationSyntax } arrow
                && arrow.ArrowToken.Span.End <= caret && te <= arrow.Parent!.Span.End) { orig = arrow; break; }
            if (n is AttributeSyntax attr && attr.Name.Span.End <= caret && n.Parent is AttributeListSyntax al && te <= al.Span.End) { orig = attr; break; }
            if (n is GlobalStatementSyntax gs && gs.SpanStart < caret && te <= gs.Span.End) { orig = gs; break; }
            if (n is MemberDeclarationSyntax or AccessorDeclarationSyntax) return null;
        }
        if (orig is null) return null;

        var (newTree, newRoot, suffix) = SnapshotTree(ctx, caret, te, policy, ct);
        var node = newRoot.FindToken(orig.SpanStart).Parent?.AncestorsAndSelf().FirstOrDefault(n => n.RawKind == orig.RawKind && n.SpanStart == orig.SpanStart);
        if (node is null || node.Span.End < caret) return null;

        SemanticModel? spec = null;
        bool ok;
        IReadOnlyList<SyntaxNode> scope = [node];
        IReadOnlyList<string>? implicitNames = null;
        Func<SyntaxReference, bool>? localAllowed = null;
        try
        {
            switch (node)
            {
                case EqualsValueClauseSyntax evc:
                    ok = ctx.Model.TryGetSpeculativeSemanticModel(((EqualsValueClauseSyntax)orig).EqualsToken.Span.End, evc, out spec);
                    break;
                case ArrowExpressionClauseSyntax arrow:
                    ok = ctx.Model.TryGetSpeculativeSemanticModel(((ArrowExpressionClauseSyntax)orig).ArrowToken.Span.End, arrow, out spec);
                    if (arrow.Parent is IndexerDeclarationSyntax ix) scope = [node, ix.ParameterList];
                    break;
                case AttributeSyntax attr:
                    ok = ctx.Model.TryGetSpeculativeSemanticModel(orig.SpanStart, attr, out spec);
                    break;
                case GlobalStatementSyntax gs:
                    ok = ctx.Model.TryGetSpeculativeSemanticModel(orig.SpanStart, gs.Statement, out spec);
                    // Top-level locals of earlier statements come from the original tree; the edited statement's own original
                    // declarations (possibly inside the hidden target) are excluded by span.
                    int editedStart = orig.SpanStart;
                    var before = ((CompilationUnitSyntax)ctx.Root).Members.OfType<GlobalStatementSyntax>().Where(g => g.Span.End <= editedStart);
                    scope = [.. before, node];
                    implicitNames = ["args"];
                    localAllowed = r => r.SyntaxTree == newTree || r.SyntaxTree == ctx.Tree && r.Span.End <= editedStart;
                    break;
                default:
                    ok = false;
                    break;
            }
        }
        catch (Exception e) when (e is NullReferenceException or ArgumentException or InvalidOperationException)
        {
            ok = false;
        }
        if (!ok || spec is null) return null;
        var errors = node.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error);
        var binding = new Binding(spec, node, newTree, "speculative", errors, suffix, null,
            FilterLaterDeclarations: policy == VisibilityPolicy.StrictPrefix, MinPosition: node.SpanStart)
        { ScopeNodes = scope, ImplicitNames = implicitNames };
        return localAllowed is null ? binding : binding with { LocalAllowed = localAllowed };
    }

    /// <summary>
    /// Caret at the start of a statement (or inside its first token) in a member body: nothing of the edited statement needs
    /// binding, only the scope at the statement start, where all preceding text is identical to the snapshot. Lookups run in
    /// the original model (whose method binding is cached for the whole file); locals declared by the edited statement itself
    /// end after the anchor and are excluded; no expression facts (receiver/expected type) are produced.
    /// </summary>
    static Binding? TryStatementStart(FileSemanticContext ctx, FlcSampleRecord s, string policy, SyntaxNode host)
    {
        int caret = s.CaretUtf16Offset;
        var token = ctx.Root.FindToken(caret);
        var stmt = token.Parent?.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault(x => x is not BlockSyntax);
        if (stmt is null || !host.Span.Contains(stmt.Span)) return null;
        var first = stmt.GetFirstToken();
        if (caret < stmt.SpanStart || caret > first.Span.End) return null;
        // Labeled/local-function statements have their own scopes; keep them on the binding engines.
        if (stmt is LabeledStatementSyntax or LocalFunctionStatementSyntax) return null;
        int qpos = stmt.SpanStart;
        var enclosing = ctx.Model.GetEnclosingSymbol(qpos);
        if (enclosing is null) return null;
        return new Binding(ctx.Model, ctx.Root, ctx.Tree, "statement_scope", 0, null, null,
            FilterLaterDeclarations: policy == VisibilityPolicy.StrictPrefix)
        {
            QueryPosition = qpos, ScopeOnly = true, FixedEnclosing = enclosing, HideEditedDeclarations = true,
            ScopeNodes = [host], ImplicitNames = host is AccessorDeclarationSyntax a
                && a.Keyword.Kind() is SyntaxKind.SetKeyword or SyntaxKind.InitKeyword or SyntaxKind.AddKeyword or SyntaxKind.RemoveKeyword ? ["value"] : null,
            LocalAllowed = r => r.SyntaxTree == ctx.Tree && r.Span.Start < qpos, // enclosing foreach/using/catch declarations start earlier
        };
    }

    /// <summary>
    /// Carets at type/namespace/compilation-unit level (member headers, using directives, declarations between members):
    /// lookups run in the original compilation at an anchor of the same scope that precedes the caret (type open brace,
    /// namespace start), expressions of the snapshot are bound speculatively there, and symbols whose original declaration
    /// overlaps the edited span are dropped. The original edited line itself is never bound.
    /// </summary>
    static Binding? TrySpeculativeScope(FileSemanticContext ctx, FlcSampleRecord s, string policy, CancellationToken ct)
    {
        int caret = s.CaretUtf16Offset, te = s.TargetEndUtf16Offset;
        var (newTree, newRoot, suffix) = SnapshotTree(ctx, caret, te, policy, ct);
        // The last token that ends before the caret (indentation belongs to the next token's leading trivia).
        var tok = caret > 0 ? newRoot.FindToken(caret - 1) : newRoot.FindToken(caret);
        if (tok.SpanStart >= caret) tok = tok.GetPreviousToken();
        SyntaxNode? scopeNode = null;
        foreach (var n in tok.Parent?.AncestorsAndSelf() ?? [])
        {
            // Executable or initializer contexts need real binding: leave them to the other engines.
            if (n is BlockSyntax or ArrowExpressionClauseSyntax or EqualsValueClauseSyntax or GlobalStatementSyntax or AttributeArgumentListSyntax
                or StatementSyntax && n.Span.End >= caret) return null; // completed constructs before the caret are fine
            if (n is BaseTypeDeclarationSyntax t && !t.OpenBraceToken.IsMissing && t.OpenBraceToken.Span.End <= caret
                && (t.CloseBraceToken.IsMissing || caret <= t.CloseBraceToken.SpanStart)) { scopeNode = t; break; }
            if (n is BaseNamespaceDeclarationSyntax) { scopeNode = n; break; }
        }
        int qpos;
        ISymbol? enclosing;
        if (scopeNode is not null)
        {
            var orig = ctx.Root.FindToken(scopeNode.SpanStart).Parent?.AncestorsAndSelf()
                .FirstOrDefault(x => x.RawKind == scopeNode.RawKind && x.SpanStart == scopeNode.SpanStart);
            if (orig is null) return null;
            qpos = orig switch
            {
                BaseTypeDeclarationSyntax ot => ot.OpenBraceToken.Span.End,
                FileScopedNamespaceDeclarationSyntax fs => fs.SemicolonToken.Span.End,
                NamespaceDeclarationSyntax ns => ns.OpenBraceToken.Span.End,
                _ => -1,
            };
            enclosing = ctx.Model.GetDeclaredSymbol(orig, ct);
            if (enclosing is null) return null;
        }
        else
        {
            qpos = 0;
            enclosing = ctx.Model.Compilation.GlobalNamespace;
        }
        if (qpos < 0 || qpos > caret) return null;
        var errors = newTree.GetDiagnostics(ct).Count(d => d.Severity == DiagnosticSeverity.Error);
        return new Binding(ctx.Model, newRoot, newTree, "speculative_scope", errors, suffix, null,
            FilterLaterDeclarations: policy == VisibilityPolicy.StrictPrefix)
        { QueryPosition = qpos, ScopeOnly = true, FixedEnclosing = enclosing, HideEditedDeclarations = true };
    }

    static string FallbackReason(SyntaxNode root, int caret)
    {
        foreach (var n in root.FindToken(caret).Parent?.AncestorsAndSelf() ?? [])
        {
            switch (n)
            {
                case BaseMethodDeclarationSyntax or AccessorDeclarationSyntax: return "member_header";
                case GlobalStatementSyntax: return "top_level_statement";
                case AttributeListSyntax: return "attribute";
                case UsingDirectiveSyntax: return "using_directive";
                case FieldDeclarationSyntax or EventFieldDeclarationSyntax: return "field";
                case PropertyDeclarationSyntax: return "property";
                case BaseTypeDeclarationSyntax: return "type_level";
                case BaseNamespaceDeclarationSyntax: return "namespace_level";
            }
        }
        return "other";
    }

    /// <summary>Innermost method/constructor/operator/accessor whose body (block or expression) contains [caret, te].</summary>
    static SyntaxNode? FindBodyHost(SyntaxNode root, int caret, int te, out int anchor, out int accessorIndex)
    {
        anchor = -1;
        accessorIndex = -1;
        var token = root.FindToken(caret);
        foreach (var n in token.Parent?.AncestorsAndSelf() ?? [])
        {
            if (n is AccessorDeclarationSyntax acc)
            {
                accessorIndex = acc.Parent is AccessorListSyntax list ? list.Accessors.IndexOf(acc) : -1;
                return InBody(acc.Body, acc.ExpressionBody, acc.Span.End, caret, te, out anchor) ? acc : null;
            }
            if (n is BaseMethodDeclarationSyntax m)
                return InBody(m.Body, m.ExpressionBody, m.Span.End, caret, te, out anchor) ? m : null;
            if (n is MemberDeclarationSyntax or GlobalStatementSyntax or CompilationUnitSyntax) return null;
        }
        return null;

        static bool InBody(BlockSyntax? body, ArrowExpressionClauseSyntax? arrow, int memberEnd, int caret, int te, out int anchor)
        {
            anchor = -1;
            if (body is not null)
            {
                anchor = body.OpenBraceToken.Span.End;
                return !body.OpenBraceToken.IsMissing && anchor <= caret && (body.CloseBraceToken.IsMissing || te <= body.CloseBraceToken.SpanStart);
            }
            if (arrow is not null)
            {
                anchor = arrow.ArrowToken.Span.End;
                return anchor <= caret && te <= memberEnd;
            }
            return false;
        }
    }

    static string KindOf(ISymbol s) => s switch
    {
        IFieldSymbol f => f.IsConst ? "const" : "field",
        IPropertySymbol => "property",
        IEventSymbol => "event",
        IMethodSymbol => "method",
        INamedTypeSymbol => "type",
        INamespaceSymbol => "namespace",
        _ => "other",
    };

    /// <summary>Fact for an overload group: the representative is the ordinal-smallest signature (order-independent).</summary>
    static SymbolFact GroupFact(List<ISymbol> symbols, Func<ITypeSymbol?, string> min, Func<ISymbol, string> sig)
    {
        var rep = symbols.Count == 1 ? symbols[0]
            : symbols.OrderBy(s => s is IMethodSymbol ? sig(s) : s.Name, StringComparer.Ordinal).First();
        return MemberFact(rep, min, sig) with { Overloads = symbols.Count };
    }

    /// <summary>Names declared anywhere inside a scope node (parameters, locals, loop/catch/pattern/query variables).</summary>
    static void CollectDeclaredNames(SyntaxNode node, ISet<string> names, int before)
    {
        // Declarations at/after the caret are never usable facts, so their names need no lookup.
        foreach (var n in node.DescendantNodesAndSelf(c => c.SpanStart < before))
        {
            if (n.SpanStart >= before) continue;
            switch (n)
            {
                case VariableDeclaratorSyntax v: names.Add(v.Identifier.ValueText); break;
                case ParameterSyntax p: names.Add(p.Identifier.ValueText); break;
                case ForEachStatementSyntax f: names.Add(f.Identifier.ValueText); break;
                case CatchDeclarationSyntax { Identifier.RawKind: not 0 } c: names.Add(c.Identifier.ValueText); break;
                case SingleVariableDesignationSyntax d: names.Add(d.Identifier.ValueText); break;
                case FromClauseSyntax q: names.Add(q.Identifier.ValueText); break;
                case LetClauseSyntax q: names.Add(q.Identifier.ValueText); break;
                case JoinClauseSyntax q: names.Add(q.Identifier.ValueText); if (q.Into is { } into) names.Add(into.Identifier.ValueText); break;
                case QueryContinuationSyntax q: names.Add(q.Identifier.ValueText); break;
            }
        }
        names.Remove("");
    }

    /// <summary>Part of a declaration that defines the symbol's identity and signature (excludes bodies and initializers).</summary>
    static TextSpan HeaderSpan(SyntaxNode n)
    {
        SyntaxNode? body = n switch
        {
            BaseMethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
            PropertyDeclarationSyntax p => (SyntaxNode?)p.AccessorList ?? p.ExpressionBody,
            IndexerDeclarationSyntax i => (SyntaxNode?)i.AccessorList ?? i.ExpressionBody,
            BasePropertyDeclarationSyntax bp => bp.AccessorList,
            _ => null,
        };
        if (body is not null) return TextSpan.FromBounds(n.SpanStart, body.SpanStart);
        return n switch
        {
            VariableDeclaratorSyntax v => TextSpan.FromBounds(v.Parent?.Parent?.SpanStart ?? v.SpanStart, v.Identifier.Span.End),
            BaseTypeDeclarationSyntax t when !t.OpenBraceToken.IsMissing => TextSpan.FromBounds(t.SpanStart, t.OpenBraceToken.SpanStart),
            _ => n.Span,
        };
    }

    static SyntaxToken DeclaredIdentifier(SyntaxNode n) => n switch
    {
        VariableDeclaratorSyntax v => v.Identifier,
        PropertyDeclarationSyntax p => p.Identifier,
        MethodDeclarationSyntax m => m.Identifier,
        EventDeclarationSyntax e => e.Identifier,
        BaseTypeDeclarationSyntax t => t.Identifier,
        DelegateDeclarationSyntax d => d.Identifier,
        ParameterSyntax p => p.Identifier,
        LocalFunctionStatementSyntax f => f.Identifier,
        SingleVariableDesignationSyntax d => d.Identifier,
        ForEachStatementSyntax f => f.Identifier,
        CatchDeclarationSyntax c => c.Identifier,
        FromClauseSyntax q => q.Identifier,
        LetClauseSyntax q => q.Identifier,
        JoinClauseSyntax q => q.Identifier,
        JoinIntoClauseSyntax q => q.Identifier,
        QueryContinuationSyntax q => q.Identifier,
        TypeParameterSyntax t => t.Identifier,
        _ => default,
    };

    /// <summary>The edit must not touch the member header (speculation requires an identical signature).</summary>
    static bool SameSignature(SyntaxNode a, SyntaxNode b) => (a, b) switch
    {
        (MethodDeclarationSyntax x, MethodDeclarationSyntax y) => x.Identifier.ValueText == y.Identifier.ValueText
            && x.ParameterList.IsEquivalentTo(y.ParameterList) && x.ReturnType.IsEquivalentTo(y.ReturnType)
            && (x.TypeParameterList?.IsEquivalentTo(y.TypeParameterList) ?? y.TypeParameterList is null),
        (BaseMethodDeclarationSyntax x, BaseMethodDeclarationSyntax y) => x.ParameterList.IsEquivalentTo(y.ParameterList),
        (AccessorDeclarationSyntax x, AccessorDeclarationSyntax y) => x.Keyword.IsKind(y.Keyword.Kind())
            && x.Parent?.Parent is BasePropertyDeclarationSyntax px && y.Parent?.Parent is BasePropertyDeclarationSyntax py && px.Type.IsEquivalentTo(py.Type),
        _ => false,
    };

    /// <summary>"\n" + one '}' per unclosed '{' token (lexer only, so braces in strings/comments do not count).</summary>
    public static string BraceClosure(string prefix)
    {
        int depth = 0;
        foreach (var t in SyntaxFactory.ParseTokens(prefix, options: CaretExtractor.ParseOptions))
        {
            if (t.IsKind(SyntaxKind.OpenBraceToken)) depth++;
            else if (t.IsKind(SyntaxKind.CloseBraceToken) && depth > 0) depth--;
        }
        return depth == 0 ? "" : "\n" + new string('}', depth);
    }

    /// <summary>
    /// Type to report for a local. Roslyn types every `var` local as nullable-annotated, which is not a fact about the value;
    /// for `var` we report the initializer's (or foreach element's) type and its annotation, otherwise the declared type.
    /// </summary>
    static (ITypeSymbol Type, string? Annotation) LocalType(SemanticModel model, ILocalSymbol l, SyntaxNode decl, CancellationToken ct)
    {
        TypeSyntax? declaredType = decl switch
        {
            VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax vd } => vd.Type,
            ForEachStatementSyntax fe => fe.Type,
            SingleVariableDesignationSyntax { Parent: DeclarationExpressionSyntax de } => de.Type,
            _ => null,
        };
        if (declaredType is { IsVar: true })
        {
            ITypeSymbol? inferred = decl switch
            {
                VariableDeclaratorSyntax { Initializer.Value: { } init } => model.GetTypeInfo(init, ct).Type,
                ForEachStatementSyntax fe => model.GetForEachStatementInfo(fe).ElementType,
                _ => null,
            };
            if (inferred is not null && inferred.TypeKind != TypeKind.Error) return (inferred, Ann(inferred));
            var stripped = l.Type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
            return (stripped, l.Type.IsValueType ? null : "none");
        }
        return (l.Type, Ann(l.Type));
    }

    /// <summary>Identifier as it must be written in code (@event for keywords).</summary>
    static string Esc(string name) => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

    /// <summary>
    /// A symbol declared after the caret in the snapshot is kept only if the original document declares a symbol of the same
    /// kind and name at the corresponding (shifted) location. Otherwise it exists only because error recovery re-interpreted
    /// code after the caret (e.g. a method body parsed as fields once its header line was removed): an artifact that would
    /// also smuggle right-context text into the payload. The original is used solely to drop facts, never to add them.
    /// </summary>
    static bool IsRecoveryArtifact(ISymbol s, SyntaxTree tree, int pos, int shift, SemanticModel? original, CancellationToken ct)
    {
        foreach (var r in s.DeclaringSyntaxReferences)
        {
            if (r.SyntaxTree != tree || r.Span.Start < pos) continue;
            if (original is null) return true; // strict_prefix: nothing real exists after the caret
            var snapNode = r.GetSyntax(ct);
            var span = new Microsoft.CodeAnalysis.Text.TextSpan(r.Span.Start + shift, r.Span.Length);
            var origRoot = original.SyntaxTree.GetRoot(ct);
            if (span.End > origRoot.FullSpan.End) return true;
            var origNode = origRoot.FindNode(span, getInnermostNodeForTie: true);
            var origSymbol = origNode.RawKind == snapNode.RawKind ? original.GetDeclaredSymbol(origNode, ct) : null;
            // Positional record properties are declared by their primary-constructor parameter syntax.
            bool sameKind = origSymbol is not null && (origSymbol.Kind == s.Kind || origSymbol is IParameterSymbol && s is IPropertySymbol);
            if (origSymbol is null || !sameKind || origSymbol.Name != s.Name) return true;
        }
        return false;
    }

    static IEnumerable<ISymbol> CandidatesOf(SymbolInfo info) => info.Symbol is not null ? [info.Symbol] : info.CandidateSymbols;

    static bool IsSelfOrBase(INamedTypeSymbol type, INamedTypeSymbol candidate)
    {
        for (INamedTypeSymbol? t = type; t is not null; t = t.ContainingType)
            for (var b = t; b is not null; b = b.BaseType)
                if (SymbolEqualityComparer.Default.Equals(b.OriginalDefinition, candidate.OriginalDefinition)) return true;
        return false;
    }

    static string? Ann(ITypeSymbol? t) => t is null || t.IsValueType ? null : t.NullableAnnotation switch
    {
        NullableAnnotation.Annotated => "annotated",
        NullableAnnotation.NotAnnotated => "not_annotated",
        _ => "none",
    };

    static int KindOrder(string kind) => kind switch { "field" => 0, "property" => 1, "event" => 2, "method" => 3, _ => 4 };

    static string KindName(ISymbol s) => s switch
    {
        IMethodSymbol { MethodKind: MethodKind.AnonymousFunction } => "lambda",
        IMethodSymbol { MethodKind: MethodKind.LocalFunction } => "local_function",
        IMethodSymbol { MethodKind: MethodKind.Constructor } => "constructor",
        IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet } => "accessor",
        IMethodSymbol => "method",
        IPropertySymbol => "property",
        IFieldSymbol => "field",
        INamedTypeSymbol => "type",
        INamespaceSymbol => "namespace",
        _ => s.Kind.ToString().ToLowerInvariant(),
    };

    static SymbolFact MemberFact(ISymbol s, Func<ITypeSymbol?, string> min, Func<ISymbol, string> sig) => s switch
    {
        IFieldSymbol f => new SymbolFact { Name = Esc(f.Name), Kind = f.IsConst ? "const" : "field", Type = min(f.Type), NullableAnnotation = Ann(f.Type), IsStatic = f.IsStatic },
        IPropertySymbol p => new SymbolFact { Name = Esc(p.Name), Kind = "property", Type = min(p.Type), NullableAnnotation = Ann(p.Type), IsStatic = p.IsStatic },
        IEventSymbol e => new SymbolFact { Name = Esc(e.Name), Kind = "event", Type = min(e.Type), IsStatic = e.IsStatic },
        IMethodSymbol m => new SymbolFact { Name = Esc(m.Name), Kind = "method", Type = min(m.ReturnType), Signature = sig(m), IsStatic = m.IsStatic },
        INamedTypeSymbol t => new SymbolFact { Name = t.Name, Kind = "type", Signature = t.TypeKind.ToString().ToLowerInvariant(), IsStatic = true },
        INamespaceSymbol n => new SymbolFact { Name = n.Name, Kind = "namespace", IsStatic = true },
        _ => new SymbolFact { Name = s.Name, Kind = s.Kind.ToString().ToLowerInvariant() },
    };

    static (string?, string?) ExpectedType(SemanticModel model, SyntaxNode root, SyntaxToken tokenBefore, int pos, ISymbol? member, ISymbol? enclosing,
        Func<ITypeSymbol?, string> min, CancellationToken ct)
    {
        ITypeSymbol? Unwrap(IMethodSymbol m)
        {
            var rt = m.ReturnType;
            if (m.IsAsync && rt is INamedTypeSymbol { IsGenericType: true, Name: "Task" or "ValueTask" } nt) return nt.TypeArguments[0];
            if (m.IsAsync && rt is INamedTypeSymbol { Name: "Task" or "ValueTask" }) return null;
            return rt.SpecialType == SpecialType.System_Void ? null : rt;
        }
        static bool Ok(ITypeSymbol? t) => t is not null && t.TypeKind != TypeKind.Error;

        switch (tokenBefore.Kind())
        {
            case SyntaxKind.ReturnKeyword:
            {
                var m = enclosing as IMethodSymbol;
                if (m is { MethodKind: MethodKind.AnonymousFunction }) return (null, null);
                if (m is not null && Unwrap(m) is { } t && Ok(t) && !IsIterator(m)) return (min(t), "return");
                return (null, null);
            }
            case SyntaxKind.EqualsToken when tokenBefore.Parent is AssignmentExpressionSyntax asg:
            {
                var t = model.GetTypeInfo(asg.Left, ct).Type;
                return Ok(t) ? (min(t), "assignment") : (null, null);
            }
            case SyntaxKind.EqualsToken when tokenBefore.Parent is EqualsValueClauseSyntax evc:
            {
                ITypeSymbol? t = evc.Parent switch
                {
                    VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax vd } when !vd.Type.IsVar => model.GetTypeInfo(vd.Type, ct).Type,
                    PropertyDeclarationSyntax pd => model.GetTypeInfo(pd.Type, ct).Type,
                    ParameterSyntax ps when ps.Type is not null => model.GetTypeInfo(ps.Type, ct).Type,
                    _ => null,
                };
                return Ok(t) ? (min(t), "initializer") : (null, null);
            }
            case SyntaxKind.EqualsGreaterThanToken when tokenBefore.Parent is ArrowExpressionClauseSyntax:
            {
                if (member is IMethodSymbol m && Unwrap(m) is { } t && Ok(t)) return (min(t), "expression_body");
                if (member is IPropertySymbol p && Ok(p.Type)) return (min(p.Type), "expression_body");
                return (null, null);
            }
            case SyntaxKind.OpenParenToken when tokenBefore.Parent is IfStatementSyntax or WhileStatementSyntax or DoStatementSyntax:
                return ("bool", "condition");
        }
        return (null, null);
    }

    static bool IsIterator(IMethodSymbol m) =>
        m.ReturnType is INamedTypeSymbol { Name: "IEnumerable" or "IEnumerator" or "IAsyncEnumerable" or "IAsyncEnumerator" };

    /// <summary>Compact prompt form; derived solely from structured facts.</summary>
    public static string RenderPrompt(SemanticRecord r)
    {
        var sb = new StringBuilder();
        if (r.EnclosingSymbol is not null) sb.Append("IN ").Append(r.EnclosingSymbol).Append('\n');
        if (r.ReturnType is not null) sb.Append("RET ").Append(r.ReturnType).Append('\n');
        if (r.ExpectedType is not null) sb.Append("EXP ").Append(r.ExpectedType).Append(" (").Append(r.ExpectedTypeSource).Append(")\n");
        if (r.Parameters.Count > 0) sb.Append("PAR ").Append(string.Join("; ", r.Parameters.Select(p => $"{p.Name}:{p.Type}"))).Append('\n');
        if (r.Locals.Count > 0) sb.Append("LOC ").Append(string.Join("; ", r.Locals.Select(p => p.Type is null ? p.Name : $"{p.Name}:{p.Type}"))).Append('\n');
        if (r.ThisMembers.Count > 0) sb.Append("THIS ").Append(string.Join("; ", r.ThisMembers.Select(FactText))).Append('\n');
        if (r.ReceiverType is not null) sb.Append("RECV ").Append(r.ReceiverType).Append(" (").Append(r.ReceiverKind).Append(")\n");
        if (r.Members.Count > 0) sb.Append("MEM ").Append(string.Join("; ", r.Members.Select(FactText))).Append('\n');
        foreach (var t in r.ContextTypes)
            sb.Append("TYPE ").Append(t.Name).Append(": ").Append(string.Join("; ", t.Members.Select(FactText))).Append('\n');
        if (r.InvocationCandidates.Count > 0)
            sb.Append("CALL ").Append(string.Join(" | ", r.InvocationCandidates.Select(c => $"{c.Signature} @{c.ArgumentIndex}" + (c.ParameterName is null ? "" : $" {c.ParameterName}:{c.ParameterType}")))).Append('\n');
        return sb.ToString();
    }

    static string FactText(SymbolFact f) => f.Kind switch
    {
        "method" => (f.Signature ?? f.Name) + (f.Overloads > 1 ? $" (+{f.Overloads - 1})" : ""),
        "constructor" => (f.Signature ?? "new()") + (f.Overloads > 1 ? $" (+{f.Overloads - 1})" : ""),
        "type" or "namespace" => f.Name,
        _ => $"{f.Name}:{f.Type}",
    };

    /// <summary>
    /// Leakage audit. Coverage = target identifiers mentioned by the payload. Violation = a payload symbol declared in this
    /// document (local/parameter/this-member) whose name occurs in the original file only inside the hidden target span.
    /// </summary>
    static LeakageAudit Audit(SemanticRecord r, FlcSampleRecord s, FileSemanticContext ctx, HashSet<string> declaredHere)
    {
        var targetIds = CaretExtractor.Identifiers(s.TargetText).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var payloadIds = CaretExtractor.Words(r.Prompt ?? "").ToHashSet(StringComparer.Ordinal);
        var covered = targetIds.Where(payloadIds.Contains).ToList();
        var violations = new List<string>();
        foreach (var name in r.Locals.Concat(r.Parameters).Concat(r.ThisMembers).Select(f => f.Name).Where(n => declaredHere.Contains(n.TrimStart('@'))).Distinct())
            if (!ctx.OccursOutside(name.TrimStart('@'), s.CaretUtf16Offset, s.TargetEndUtf16Offset)) violations.Add(name);
        return new LeakageAudit { TargetIdentifiers = targetIds, CoveredTargetIdentifiers = covered, Violations = violations };
    }
}
