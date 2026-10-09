using System.Text.RegularExpressions;
using FlcDataset.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace FlcDataset.Extraction;

/// <summary>Per-file provenance passed to the extractor.</summary>
public sealed record FileContext(
    string RepositoryId, string? Revision, string RelativePath, string? Project, bool IsTest, string Split, string SplitGroup);

public sealed record CaretCandidate(int Offset, string Kind, string? Subkind, int Priority);

public sealed class FileExtractionResult
{
    public List<FlcSampleRecord> Samples { get; } = [];
    public List<ExclusionRecord> Exclusions { get; } = [];
    public Dictionary<string, long> Counts { get; } = new(StringComparer.Ordinal);
    public int SyntaxErrors { get; set; }
    public double ParseMs { get; set; }
    public double ExtractMs { get; set; }

    public void Count(string key, long n = 1) => Counts[key] = Counts.GetValueOrDefault(key) + n;
}

/// <summary>
/// Roslyn syntax-only caret sampling. Candidate carets are generated at token/node boundaries (plus a few identifier-internal
/// offsets), classified into strata, filtered by negative strata and quality rules, then deterministically sampled with a
/// hash of (seed, source sha, offset) so the result is independent of processing order and worker count.
/// </summary>
public sealed class CaretExtractor
{
    public static readonly CSharpParseOptions ParseOptions =
        new(LanguageVersion.Preview, DocumentationMode.Parse, SourceCodeKind.Regular);

    static readonly HashSet<string> LinqMethods = new(StringComparer.Ordinal)
    {
        "Where", "Select", "SelectMany", "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending", "GroupBy", "Join",
        "GroupJoin", "Any", "All", "First", "FirstOrDefault", "Single", "SingleOrDefault", "Last", "LastOrDefault", "Count",
        "LongCount", "Sum", "Min", "Max", "MinBy", "MaxBy", "Average", "ToList", "ToArray", "ToDictionary", "ToHashSet", "ToLookup",
        "Skip", "Take", "SkipWhile", "TakeWhile", "Distinct", "DistinctBy", "Aggregate", "Zip", "Concat", "Union", "Except",
        "Intersect", "Contains", "Include", "ThenInclude", "AsNoTracking", "ToListAsync", "ToArrayAsync", "FirstOrDefaultAsync",
        "SingleOrDefaultAsync", "AnyAsync", "CountAsync", "SumAsync", "ToDictionaryAsync", "AsQueryable", "AsEnumerable",
        "OfType", "Cast", "Chunk", "Append", "Prepend", "Reverse", "DefaultIfEmpty", "SequenceEqual", "ElementAt",
    };

    static readonly HashSet<string> LogMethods = new(StringComparer.Ordinal)
    {
        "Log", "LogTrace", "LogDebug", "LogInformation", "LogWarning", "LogError", "LogCritical",
    };

    static readonly Regex IdentifierRx = new(@"[\p{L}_][\p{L}\p{Nd}_]*", RegexOptions.CultureInvariant);

    readonly DatasetConfig _config;
    readonly string _seed;
    readonly IReadOnlyList<Regex> _secrets;
    readonly string _configHash;

    public CaretExtractor(DatasetConfig config, IReadOnlyList<Regex>? secretPatterns = null)
    {
        _config = config;
        _seed = config.Seed.ToString();
        // Lines are checked against file-level and line-level patterns.
        _secrets = (secretPatterns ?? config.Secrets.Patterns.Select(p => new Regex(p, RegexOptions.CultureInvariant)).ToArray())
            .Concat(config.Secrets.LinePatterns.Select(p => new Regex(p, RegexOptions.CultureInvariant))).ToArray();
        _configHash = config.Hash();
    }

    public static SyntaxTree Parse(SourceDocument doc, string path) =>
        CSharpSyntaxTree.ParseText(SourceText.From(doc.Text), ParseOptions, path);

    public FileExtractionResult Extract(SourceDocument doc, FileContext ctx)
    {
        var result = new FileExtractionResult();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tree = Parse(doc, ctx.RelativePath);
        var root = tree.GetRoot();
        result.SyntaxErrors = tree.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error);
        result.ParseMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();

        var tokens = new TokenIndex(root);
        var tokensByLine = new List<int>[doc.Lines.Count];
        for (int i = 0; i < tokens.All.Length; i++)
        {
            var t = tokens.All[i];
            if (t.Span.Length == 0) continue; // missing tokens from error recovery, EOF
            var li = doc.LineIndexOf(t.SpanStart);
            (tokensByLine[li] ??= []).Add(i);
        }

        var selected = new List<(double Score, PendingSample Sample)>();
        var s = _config.Sampling;

        foreach (var line in doc.Lines)
        {
            var text = doc.Text;
            int cs = line.Start;
            while (cs < line.End && IsInlineWhitespace(text[cs])) cs++;
            if (cs >= line.End) { result.Count("lines.blank"); continue; }
            int te = line.End;
            while (te > cs && IsInlineWhitespace(text[te - 1])) te--;
            result.Count("lines.nonblank");

            var lineReason = ClassifyLine(tokens, cs);
            if (lineReason is not null)
            {
                result.Count("lines." + lineReason);
                AddExclusion(result, doc, ctx, cs, lineReason, "line_start", line);
                continue;
            }
            if (line.End - line.Start > s.MaxLineChars)
            {
                result.Count("lines.too_long");
                AddExclusion(result, doc, ctx, cs, "line_too_long", "line_start", line);
                continue;
            }

            var candidates = new Dictionary<int, CaretCandidate>();
            void Add(int offset, string kind, string? sub, int prio)
            {
                if (offset < cs || offset >= te) return;
                if (!candidates.TryGetValue(offset, out var existing) || existing.Priority < prio)
                    candidates[offset] = new CaretCandidate(offset, kind, sub, prio);
            }

            Add(cs, "line_start", LineStartSubkind(tokens, cs), 50);
            var lineTokens = tokensByLine[line.Number] ?? [];
            foreach (var ti in lineTokens) GenerateTokenCandidates(tokens.All[ti], tokens.Next(ti), Add, result, doc, ctx, line);

            foreach (var cand in candidates.Values.OrderBy(c => c.Offset))
            {
                result.Count("candidates.total");
                result.Count(Key("candidates.kind.", cand.Kind));
                var pending = Evaluate(doc, ctx, root, tokens, line, cand, te, result);
                if (pending is null) continue;
                result.Count("candidates.eligible");
                result.Count(Key("candidates.eligible_kind.", pending.Kind));
                // Weighted deterministic sampling: accept when the hash score falls under the stratum weight.
                var weight = s.Weights.GetValueOrDefault(pending.Kind, 0);
                if (pending.Trivial) weight *= s.TrivialTargetKeepProbability;
                if (weight <= 0) { result.Count("candidates.not_sampled"); continue; }
                var score = Hashing.CaretUniform(_seed, doc.Sha256, pending.Offset);
                if (score >= weight) { result.Count("candidates.not_sampled"); continue; }
                selected.Add((score / weight, pending));
            }
        }

        // Caps: per line, then per file; lowest normalized score wins (deterministic), output in source order.
        // Records (with context strings) are materialized only for the survivors, keeping cost linear in kept samples.
        var capped = selected
            .GroupBy(x => x.Sample.Line.Number)
            .SelectMany(g => g.OrderBy(x => x.Score).ThenBy(x => x.Sample.Offset).Take(s.MaxSamplesPerLine))
            .OrderBy(x => x.Score).ThenBy(x => x.Sample.Offset)
            .Take(s.MaxSamplesPerFile)
            .Select(x => x.Sample)
            .OrderBy(x => x.Offset)
            .ToList();
        result.Count("candidates.capped", selected.Count - capped.Count);
        result.Samples.AddRange(capped.Select(c => Materialize(doc, ctx, tokens, c, result.SyntaxErrors)));
        result.ExtractMs = sw.Elapsed.TotalMilliseconds;
        return result;
    }

    /// <summary>
    /// Flat, ordered token array of a file. Roslyn's GetNextToken/GetPreviousToken and DescendantTokens(span) scan sibling
    /// lists linearly, which is quadratic for files with very long statement lists; index arithmetic here is O(1)/O(log n).
    /// </summary>
    sealed class TokenIndex
    {
        public readonly SyntaxToken[] All;
        readonly int[] _fullEnd;

        public TokenIndex(SyntaxNode root)
        {
            All = root.DescendantTokens(descendIntoTrivia: false).ToArray();
            _fullEnd = All.Select(t => t.FullSpan.End).ToArray();
        }

        /// <summary>Next non-missing token after index i (EOF if none).</summary>
        public SyntaxToken Next(int i)
        {
            for (int j = i + 1; j < All.Length; j++) if (All[j].Span.Length > 0 || All[j].IsKind(SyntaxKind.EndOfFileToken)) return All[j];
            return All[^1];
        }

        /// <summary>Token whose span ends at or before position (the token "before the caret").</summary>
        public SyntaxToken Previous(int position)
        {
            int idx = IndexContaining(position);
            for (int j = idx; j >= 0; j--) if (All[j].Span.Length > 0 && All[j].Span.End <= position) return All[j];
            return default;
        }

        int IndexContaining(int position)
        {
            int i = Array.BinarySearch(_fullEnd, position);
            i = i >= 0 ? i + 1 : ~i; // first token with FullSpan.End > position
            return Math.Min(i, All.Length - 1);
        }

        /// <summary>Equivalent of SyntaxNode.FindToken(position): the token whose full span contains the position.</summary>
        public SyntaxToken Find(int position) => All[IndexContaining(position)];

        /// <summary>Equivalent of SyntaxNode.FindTrivia(position) for non-structured lookup.</summary>
        public SyntaxTrivia FindTrivia(int position)
        {
            var t = Find(position);
            foreach (var tr in t.LeadingTrivia) if (tr.FullSpan.Contains(position)) return tr;
            foreach (var tr in t.TrailingTrivia) if (tr.FullSpan.Contains(position)) return tr;
            return default;
        }

        /// <summary>Tokens whose full span intersects [start, end).</summary>
        public IEnumerable<SyntaxToken> Intersecting(int start, int end)
        {
            for (int i = IndexContaining(start); i < All.Length && All[i].FullSpan.Start < end; i++) yield return All[i];
        }
    }

    static bool IsInlineWhitespace(char c) => c is ' ' or '\t' or '\f' or '\v' || (char.IsWhiteSpace(c) && !SourceDocument.IsLineBreakChar(c));

    /// <summary>Negative strata at line level: comment lines, preprocessor, inactive code, interior of multi-line strings.</summary>
    static string? ClassifyLine(TokenIndex tokens, int cs)
    {
        var trivia = tokens.FindTrivia(cs);
        if (trivia.Span.Contains(cs))
        {
            switch (trivia.Kind())
            {
                case SyntaxKind.SingleLineCommentTrivia:
                case SyntaxKind.MultiLineCommentTrivia: return "comment";
                case SyntaxKind.SingleLineDocumentationCommentTrivia:
                case SyntaxKind.MultiLineDocumentationCommentTrivia: return "doc_comment";
                case SyntaxKind.DisabledTextTrivia: return "inactive_preprocessor_code";
            }
            if (trivia.IsDirective) return "preprocessor_directive";
        }
        var token = tokens.Find(cs);
        if (token.SpanStart < cs && token.Span.End > cs)
        {
            return token.Kind() switch
            {
                SyntaxKind.MultiLineRawStringLiteralToken => "inside_raw_string",
                SyntaxKind.StringLiteralToken => "inside_multiline_string",
                SyntaxKind.InterpolatedStringTextToken => "inside_interpolated_string",
                _ => "inside_multiline_token",
            };
        }
        if (token.Parent?.FirstAncestorOrSelf<InterpolatedStringExpressionSyntax>() is { } interp && interp.SpanStart < cs)
            return "inside_interpolated_string";
        return null;
    }

    static string LineStartSubkind(TokenIndex tokens, int cs)
    {
        var token = tokens.Find(cs);
        if (token.IsKind(SyntaxKind.OpenBraceToken) || token.IsKind(SyntaxKind.CloseBraceToken)) return "brace";
        SyntaxNode? owner = token.Parent?.AncestorsAndSelf().FirstOrDefault(n =>
            n is StatementSyntax or MemberDeclarationSyntax or UsingDirectiveSyntax or AttributeListSyntax or SwitchLabelSyntax
              or BaseNamespaceDeclarationSyntax or AccessorDeclarationSyntax or SwitchExpressionArmSyntax);
        if (owner is null) return "other";
        if (owner.SpanStart < cs) return "continuation";
        return owner switch
        {
            LocalDeclarationStatementSyntax => "local_declaration",
            ExpressionStatementSyntax es => es.Expression is AssignmentExpressionSyntax ? "assignment" :
                es.Expression is InvocationExpressionSyntax or AwaitExpressionSyntax ? "invocation" : "expression",
            ReturnStatementSyntax => "return",
            IfStatementSyntax or SwitchStatementSyntax or ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax
                or DoStatementSyntax => "control_flow",
            TryStatementSyntax or ThrowStatementSyntax => "exception",
            UsingDirectiveSyntax => "using_directive",
            AttributeListSyntax => "attribute",
            MethodDeclarationSyntax or ConstructorDeclarationSyntax or LocalFunctionStatementSyntax => "method_declaration",
            PropertyDeclarationSyntax or FieldDeclarationSyntax or EventFieldDeclarationSyntax => "field_or_property",
            BaseTypeDeclarationSyntax or DelegateDeclarationSyntax => "type_declaration",
            BaseNamespaceDeclarationSyntax => "namespace",
            _ => owner.Kind().ToString().ToLowerInvariant(),
        };
    }

    void GenerateTokenCandidates(SyntaxToken tok, SyntaxToken next, Action<int, string, string?, int> add, FileExtractionResult result,
        SourceDocument doc, FileContext ctx, LineInfo line)
    {
        var kind = tok.Kind();
        int nextStart = next.SpanStart; // caret after typed whitespace

        // Generic boundary after any token (low weight stratum).
        add(tok.Span.End, "token_boundary", kind.ToString(), 1);

        // Strings: log templates are a positive stratum; other literals are counted negative strata.
        if (IsStringLike(kind) || tok.Parent is InterpolatedStringExpressionSyntax)
        {
            var log = LogTemplateContext(tok);
            int open = OpeningQuoteEnd(tok);
            if (log is not null && open > 0)
            {
                add(tok.SpanStart, "log_message", log + ":template_start", 95);
                add(open, "log_message", log + ":template_body", 95);
            }
            else if (open > 0 && open < Math.Min(tok.Span.End, line.End))
            {
                var reason = kind switch
                {
                    SyntaxKind.SingleLineRawStringLiteralToken or SyntaxKind.MultiLineRawStringLiteralToken => "in_raw_string",
                    SyntaxKind.InterpolatedStringStartToken or SyntaxKind.InterpolatedVerbatimStringStartToken
                        or SyntaxKind.InterpolatedSingleLineRawStringStartToken or SyntaxKind.InterpolatedMultiLineRawStringStartToken => "in_interpolated_string",
                    SyntaxKind.CharacterLiteralToken => "in_char_literal",
                    _ => "in_string",
                };
                result.Count("candidates.total");
                result.Count("excluded." + reason);
                AddExclusion(result, doc, ctx, open, reason, "string_content", line);
            }
        }

        // Trailing comment on a code line: count as negative stratum.
        foreach (var tr in tok.TrailingTrivia)
        {
            if (tr.IsKind(SyntaxKind.SingleLineCommentTrivia) && tr.Span.End <= line.End && tr.Span.Length > 2)
            {
                result.Count("candidates.total");
                result.Count("excluded.in_comment");
                AddExclusion(result, doc, ctx, tr.SpanStart + 2, "in_comment", "comment_content", line);
            }
        }

        switch (kind)
        {
            case SyntaxKind.IdentifierToken when tok.Span.Length >= 2:
                foreach (var k in IdentifierPrefixOffsets(tok.Text))
                    add(tok.SpanStart + k, "identifier_partial", k <= _config.Sampling.IdentifierPrefixMax ? "prefix" : "hump", 10);
                break;
            case SyntaxKind.DotToken when tok.Parent is MemberAccessExpressionSyntax:
                add(tok.Span.End, "member_access", "dot", 80);
                break;
            case SyntaxKind.DotToken when tok.Parent is MemberBindingExpressionSyntax:
                add(tok.Span.End, "member_access", "conditional_dot", 80);
                break;
            case SyntaxKind.DotToken when tok.Parent is QualifiedNameSyntax:
                add(tok.Span.End, "member_access", "qualified_name", 60);
                break;
            case SyntaxKind.MinusGreaterThanToken:
                add(tok.Span.End, "member_access", "pointer", 60);
                break;
            case SyntaxKind.OpenParenToken when tok.Parent is ArgumentListSyntax:
                add(tok.Span.End, "argument_list", "open_paren", 70);
                break;
            case SyntaxKind.OpenBracketToken when tok.Parent is BracketedArgumentListSyntax:
                add(tok.Span.End, "argument_list", "open_bracket", 65);
                break;
            case SyntaxKind.CommaToken when tok.Parent is ArgumentListSyntax or BracketedArgumentListSyntax:
                add(nextStart, "argument_list", "after_comma", 70);
                break;
            case SyntaxKind.OpenParenToken when tok.Parent is ParameterListSyntax:
                add(tok.Span.End, "argument_list", "parameter_list", 40);
                break;
            case SyntaxKind.OpenParenToken when tok.Parent is IfStatementSyntax or WhileStatementSyntax or ForEachStatementSyntax
                or ForStatementSyntax or SwitchStatementSyntax or CatchDeclarationSyntax or UsingStatementSyntax or LockStatementSyntax
                or CatchFilterClauseSyntax or DoStatementSyntax:
                add(tok.Span.End, "control_flow", tok.Parent.Kind().ToString().Replace("Statement", "").Replace("Syntax", "").ToLowerInvariant(), 75);
                break;
            case SyntaxKind.ReturnKeyword or SyntaxKind.AwaitKeyword or SyntaxKind.NewKeyword or SyntaxKind.ThrowKeyword
                or SyntaxKind.YieldKeyword or SyntaxKind.IsKeyword or SyntaxKind.AsKeyword or SyntaxKind.CaseKeyword
                or SyntaxKind.WhenKeyword or SyntaxKind.InKeyword or SyntaxKind.VarKeyword or SyntaxKind.ElseKeyword
                or SyntaxKind.NameOfKeyword or SyntaxKind.TypeOfKeyword:
                if (next.SpanStart > tok.Span.End || kind is SyntaxKind.NameOfKeyword or SyntaxKind.TypeOfKeyword)
                    add(nextStart, "after_keyword", tok.Text, 60);
                break;
            case SyntaxKind.EqualsToken:
                add(nextStart, "after_operator", tok.Parent is EqualsValueClauseSyntax ? "initializer" : "assignment", 60);
                break;
            case SyntaxKind.EqualsGreaterThanToken:
                add(nextStart, "lambda_body", tok.Parent switch
                {
                    LambdaExpressionSyntax => "lambda",
                    ArrowExpressionClauseSyntax => "expression_body",
                    SwitchExpressionArmSyntax => "switch_arm",
                    _ => "arrow",
                }, 65);
                break;
            case SyntaxKind.QuestionQuestionToken or SyntaxKind.QuestionQuestionEqualsToken or SyntaxKind.PlusEqualsToken
                or SyntaxKind.MinusEqualsToken or SyntaxKind.EqualsEqualsToken or SyntaxKind.ExclamationEqualsToken
                or SyntaxKind.AmpersandAmpersandToken or SyntaxKind.BarBarToken or SyntaxKind.LessThanEqualsToken
                or SyntaxKind.GreaterThanEqualsToken or SyntaxKind.PlusToken when tok.Parent is BinaryExpressionSyntax or AssignmentExpressionSyntax:
                add(nextStart, "after_operator", tok.Text, 45);
                break;
            case SyntaxKind.QuestionToken when tok.Parent is ConditionalExpressionSyntax:
            case SyntaxKind.ColonToken when tok.Parent is ConditionalExpressionSyntax:
                add(nextStart, "after_operator", "conditional", 45);
                break;
            case SyntaxKind.OpenBraceToken when tok.Parent is InitializerExpressionSyntax:
                add(nextStart, "after_operator", "initializer_brace", 40);
                break;
        }

        // [LoggerMessage(..., Message = "...")] attribute argument.
        if (kind == SyntaxKind.EqualsToken && tok.Parent is NameEqualsSyntax { Name.Identifier.ValueText: "Message" } ne
            && ne.Parent is AttributeArgumentSyntax { Parent.Parent: AttributeSyntax attr } && IsLoggerMessageAttribute(attr))
            add(nextStart, "log_message", "logger_message_attribute:template_start", 95);
    }

    /// <summary>Typed-prefix offsets inside an identifier: 1..N chars plus camel-hump boundaries. Never splits surrogate pairs.</summary>
    IEnumerable<int> IdentifierPrefixOffsets(string ident)
    {
        var set = new SortedSet<int>();
        for (int k = 1; k <= Math.Min(_config.Sampling.IdentifierPrefixMax, ident.Length - 1); k++) set.Add(k);
        for (int k = 2; k < ident.Length; k++)
            if (char.IsUpper(ident[k]) && char.IsLower(ident[k - 1])) set.Add(k);
        return set.Where(k => !char.IsLowSurrogate(ident[k]));
    }

    static bool IsStringLike(SyntaxKind k) => k is SyntaxKind.StringLiteralToken or SyntaxKind.Utf8StringLiteralToken
        or SyntaxKind.SingleLineRawStringLiteralToken or SyntaxKind.MultiLineRawStringLiteralToken or SyntaxKind.CharacterLiteralToken
        or SyntaxKind.InterpolatedStringStartToken or SyntaxKind.InterpolatedVerbatimStringStartToken
        or SyntaxKind.InterpolatedSingleLineRawStringStartToken or SyntaxKind.InterpolatedMultiLineRawStringStartToken;

    /// <summary>Offset just after the opening quote(s) of a literal token, or -1.</summary>
    static int OpeningQuoteEnd(SyntaxToken tok)
    {
        var t = tok.Text;
        int i = 0;
        while (i < t.Length && t[i] is '$' or '@' or 'u') i++;
        int q = 0;
        while (i + q < t.Length && t[i + q] is '"' or '\'') q++;
        if (q == 0) return -1;
        return tok.SpanStart + i + (q >= 3 ? q : 1);
    }

    /// <summary>Returns the logging API flavor when the literal is the message template of a logging call.</summary>
    static string? LogTemplateContext(SyntaxToken tok)
    {
        if (tok.Kind() is not (SyntaxKind.StringLiteralToken or SyntaxKind.InterpolatedStringStartToken)) return null;
        SyntaxNode? expr = tok.Parent;
        if (expr is null) return null;
        if (expr.Parent is AttributeArgumentSyntax { NameEquals.Name.Identifier.ValueText: "Message", Parent.Parent: AttributeSyntax attr }
            && IsLoggerMessageAttribute(attr))
            return "logger_message_attribute";
        if (expr.Parent is not ArgumentSyntax arg || arg.Parent is not ArgumentListSyntax args || args.Parent is not InvocationExpressionSyntax inv)
            return null;
        var name = inv.Expression switch
        {
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
            IdentifierNameSyntax id => id.Identifier.ValueText,
            _ => null,
        };
        if (name is null || !LogMethods.Contains(name)) return null;
        // Template = first string-literal argument.
        var firstString = args.Arguments.FirstOrDefault(a => a.Expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression }
            or InterpolatedStringExpressionSyntax);
        if (firstString != arg) return null;
        return expr is InterpolatedStringExpressionSyntax ? "ilogger_interpolated" : "ilogger";
    }

    static bool IsLoggerMessageAttribute(AttributeSyntax attr)
    {
        var n = attr.Name.ToString();
        return n is "LoggerMessage" or "LoggerMessageAttribute" || n.EndsWith(".LoggerMessage", StringComparison.Ordinal);
    }

    /// <summary>Candidate that passed all exclusion rules; turned into a record only if it survives sampling and caps.</summary>
    public sealed record PendingSample(int Offset, int TargetEnd, LineInfo Line, string Kind, string? Subkind, List<string>? Tags, bool Trivial);

    PendingSample? Evaluate(SourceDocument doc, FileContext ctx, SyntaxNode root, TokenIndex tokens, LineInfo line, CaretCandidate cand, int te,
        FileExtractionResult result)
    {
        var text = doc.Text;
        int p = cand.Offset;
        string? reason = null;
        if (p > 0 && p < text.Length && char.IsLowSurrogate(text[p]) && char.IsHighSurrogate(text[p - 1])) reason = "splits_surrogate_pair";
        int targetLen = te - p;
        if (reason is null && targetLen < Math.Max(1, _config.Sampling.MinTargetChars)) reason = "empty_target";
        if (reason is null && targetLen > _config.Sampling.MaxTargetChars) reason = "target_too_long";

        // Strings/comments inside the caret position itself (not log templates).
        var token = tokens.Find(p);
        if (reason is null && cand.Kind != "log_message")
        {
            if (token.SpanStart < p && p < token.Span.End && (IsStringLike(token.Kind()) || token.IsKind(SyntaxKind.InterpolatedStringTextToken)))
                reason = "caret_inside_literal";
            else if (token.Parent?.FirstAncestorOrSelf<InterpolatedStringExpressionSyntax>() is { } interp && interp.SpanStart < p && p < interp.Span.End)
                reason = "in_interpolated_string";
            else if (tokens.FindTrivia(p) is var tr && tr.Span.Start < p && p < tr.Span.End && !tr.IsKind(SyntaxKind.WhitespaceTrivia))
                reason = "caret_inside_trivia";
        }
        // Raw / verbatim strings that continue beyond this line are excluded for the one-line dataset.
        if (reason is null)
        {
            foreach (var t in tokens.Intersecting(p, te))
            {
                if (t.Span.End > line.End && t.SpanStart < te)
                {
                    reason = t.Kind() is SyntaxKind.MultiLineRawStringLiteralToken or SyntaxKind.InterpolatedMultiLineRawStringStartToken
                        ? "raw_multiline_string" : t.Kind() is SyntaxKind.StringLiteralToken ? "multiline_string" : null;
                    if (reason is not null) break;
                }
            }
        }
        if (reason is null && _secrets.Any(rx => rx.IsMatch(text.AsSpan(line.Start, line.End - line.Start)))) reason = "secret_detected";

        if (reason is not null)
        {
            result.Count("excluded." + reason);
            AddExclusion(result, doc, ctx, p, reason, cand.Kind, line);
            return null;
        }

        var kind = cand.Kind;
        var sub = cand.Subkind;
        List<string>? tags = null;
        // Only these kinds can be re-stratified as LINQ; other tags are computed lazily for kept samples.
        if (kind is "member_access" or "argument_list" or "lambda_body")
        {
            tags = Tags(tokens, token, p, ctx);
            if (tags.Contains("linq"))
            {
                sub = kind + (sub is null ? "" : ":" + sub);
                kind = "linq";
            }
        }
        bool trivial = !text.AsSpan(p, targetLen).ContainsAnyExcept("{}()[];, ");
        return new PendingSample(p, te, line, kind, sub, tags, trivial);
    }

    List<string> Flags(SourceDocument doc, TokenIndex tokens, PendingSample ps, int syntaxErrors)
    {
        var (p, te, line) = (ps.Offset, ps.TargetEnd, ps.Line);
        var target = doc.Text.AsSpan(p, te - p);
        var flags = new List<string>();
        if (ps.Trivial) flags.Add("trivial_target");
        if (char.IsWhiteSpace(target[0])) flags.Add("target_starts_with_whitespace");
        // An editor usually auto-inserts the closer after typing the opener; such targets mismatch the live editing state.
        if (target[0] is ')' or ']' or '}') flags.Add("target_starts_with_closer");
        if (te < line.End) flags.Add("trailing_whitespace");
        if (tokens.Intersecting(p, te).SelectMany(t => t.LeadingTrivia.Concat(t.TrailingTrivia))
                .Any(t => t.SpanStart < te && t.Span.End > p && (t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia))))
            flags.Add("target_has_comment");
        if (syntaxErrors > 0) flags.Add("file_has_syntax_errors");
        if (target.ContainsAnyExceptInRange((char)0, (char)0x7F)) flags.Add("non_ascii_target");
        return flags;
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, string), string> KeyCache = new();
    static string Key(string prefix, string kind) => KeyCache.GetOrAdd((prefix, kind), k => k.Item1 + k.Item2);

    FlcSampleRecord Materialize(SourceDocument doc, FileContext ctx, TokenIndex tokens, PendingSample ps, int syntaxErrors)
    {
        var text = doc.Text;
        var (p, te, line) = (ps.Offset, ps.TargetEnd, ps.Line);
        var c = _config.Context;
        int left = Math.Max(0, p - c.LeftChars);
        if (left > 0 && char.IsLowSurrogate(text[left])) left++;
        int right = Math.Min(text.Length, te + c.RightChars);
        if (right < text.Length && right > te && char.IsHighSurrogate(text[right - 1])) right--;
        var (ln, col) = (line.Number, p - line.Start);
        int ws = line.Start;
        while (ws < line.End && IsInlineWhitespace(text[ws])) ws++;

        return new FlcSampleRecord
        {
            SampleId = Hashing.StableId(ctx.RepositoryId, ctx.RelativePath, doc.Sha256, p.ToString(), te.ToString()),
            RepositoryId = ctx.RepositoryId,
            Revision = ctx.Revision,
            RelativePath = ctx.RelativePath,
            Project = ctx.Project,
            IsTest = ctx.IsTest,
            SourceSha256 = doc.Sha256,
            CaretUtf16Offset = p,
            CaretLineZeroBased = ln,
            CaretColumnUtf16ZeroBased = col,
            CaretByteOffset = doc.ByteOffset(p),
            TargetEndUtf16Offset = te,
            TargetEndByteOffset = doc.ByteOffset(te),
            LineStartUtf16Offset = line.Start,
            LineEndUtf16Offset = line.End,
            CaretKind = ps.Kind,
            CaretSubkind = ps.Subkind,
            Tags = ps.Tags ?? Tags(tokens, tokens.Find(p), p, ctx),
            LeftContextStartUtf16Offset = left,
            LeftContextTruncated = left > 0,
            LeftContext = text[left..p],
            TargetText = text[p..te],
            RightContext = text[te..right],
            RightContextEndUtf16Offset = right,
            RightContextTruncated = right < text.Length,
            EndOfLine = line.Break switch
            {
                EndOfLine.Lf => "LF", EndOfLine.Crlf => "CRLF", EndOfLine.Cr => "CR", EndOfLine.Other => "OTHER", _ => "EOF",
            },
            Indentation = text[line.Start..ws],
            QualityFlags = Flags(doc, tokens, ps, syntaxErrors),
            Split = ctx.Split,
            SplitGroup = ctx.SplitGroup,
            SemanticStatus = SemanticStatus.NotAttempted,
            SemanticReason = null,
            ConfigVersion = _config.ConfigVersion,
            ConfigSha256 = _configHash,
        };
    }

    static List<string> Tags(TokenIndex tokens, SyntaxToken token, int p, FileContext ctx)
    {
        var tags = new SortedSet<string>(StringComparer.Ordinal);
        var prev = token.SpanStart >= p ? tokens.Previous(p) : token;
        var node = prev.Parent ?? token.Parent;
        if (ctx.IsTest) tags.Add("test_code");
        if (node is null) return [.. tags];
        foreach (var a in node.AncestorsAndSelf())
        {
            switch (a)
            {
                case QueryExpressionSyntax: tags.Add("linq"); break;
                case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ma } when LinqMethods.Contains(ma.Name.Identifier.ValueText):
                    tags.Add("linq"); break;
                case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ma2 } when LogMethods.Contains(ma2.Name.Identifier.ValueText):
                    tags.Add("log_call"); break;
                case LambdaExpressionSyntax: tags.Add("lambda"); break;
                case SwitchExpressionSyntax: tags.Add("switch_expression"); break;
                case PatternSyntax or IsPatternExpressionSyntax: tags.Add("pattern"); break;
                case InitializerExpressionSyntax: tags.Add("initializer"); break;
                case CollectionExpressionSyntax: tags.Add("collection_expression"); break;
                case AttributeListSyntax: tags.Add("attribute"); break;
                case AwaitExpressionSyntax: tags.Add("await"); break;
                case TryStatementSyntax or CatchClauseSyntax or ThrowStatementSyntax or ThrowExpressionSyntax: tags.Add("exception"); break;
                case UsingDirectiveSyntax: tags.Add("using_directive"); break;
                case RecordDeclarationSyntax: tags.Add("record"); break;
            }
            if (a is MemberDeclarationSyntax and not BaseTypeDeclarationSyntax) break;
        }
        return [.. tags];
    }

    void AddExclusion(FileExtractionResult result, SourceDocument doc, FileContext ctx, int offset, string reason, string kind, LineInfo line)
    {
        result.Exclusions.Add(new ExclusionRecord
        {
            RelativePath = ctx.RelativePath,
            CaretUtf16Offset = offset,
            CaretLineZeroBased = line.Number,
            Reason = reason,
            CandidateKind = kind,
            LineText = doc.Text[line.Start..line.End],
        });
    }

    /// <summary>
    /// C# identifiers in a code fragment, via the Roslyn lexer: string/char literal contents, comments and (contextual)
    /// keywords such as await/var are not identifiers. Used for leakage and coverage audits.
    /// </summary>
    public static IEnumerable<string> Identifiers(string text) =>
        SyntaxFactory.ParseTokens(text, options: ParseOptions)
            .Where(t => t.IsKind(SyntaxKind.IdentifierToken) && SyntaxFacts.GetContextualKeywordKind(t.ValueText) == SyntaxKind.None)
            .Select(t => t.ValueText).Distinct();

    /// <summary>Words of a prompt-like text (not necessarily valid C#): identifier-shaped runs.</summary>
    public static IEnumerable<string> Words(string text) => IdentifierRx.Matches(text).Select(m => m.Value).Distinct();
}
