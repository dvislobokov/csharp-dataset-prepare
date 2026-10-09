using System.Text;

namespace FlcDataset.Core;

/// <summary>Training/inference serialization options. Budgets are in UTF-16 chars until a tokenizer is pinned.</summary>
public sealed record PromptOptions
{
    public string Format { get; init; } = PromptRenderer.FormatV1;
    public int MaxCodeChars { get; init; } = 4000;
    public int MaxSemanticChars { get; init; } = 900;
    public bool IncludePath { get; init; } = true;
    public string Policy { get; init; } = VisibilityPolicy.EditorSnapshot;
    /// <summary>Max items per list line (members, overload candidates, locals...).</summary>
    public int MaxItemsPerLine { get; init; } = 24;
}

/// <summary>One supervised example: loss applies to <see cref="Completion"/> only.</summary>
public sealed record TrainingRecord
{
    public string SchemaVersion { get; init; } = "flc-train/v1";
    public required string SampleId { get; init; }
    public required string Split { get; init; }
    public required string PromptFormat { get; init; }
    public required string CaretKind { get; init; }
    public required string SemanticStatus { get; init; }
    public required bool HasSemantic { get; init; }
    public required string Prompt { get; init; }
    public required string Completion { get; init; }
    public required int CodeChars { get; init; }
    public required bool CodeTruncated { get; init; }
    public required int SemanticChars { get; init; }
    public required int SemanticItemsDropped { get; init; }
}

/// <summary>
/// flc-prompt/v1 — left-to-right line completion:
/// <code>
/// &lt;|cs|&gt;&lt;|path|&gt;src/X.cs\n
/// &lt;|sem|&gt;\nRET ...\nEXPECT ...\n...        (omitted when no semantic facts)
/// &lt;|code|&gt;\n...left context ending at the caret...&lt;|complete|&gt;
/// completion = target_text + &lt;|eol|&gt;
/// </code>
/// Angle-bracket markers are meant to become dedicated special-token ids. The right context is never rendered.
/// Facts come only from the semantic sidecar (computed on the target-free snapshot); nothing is read from the target.
/// </summary>
public static class PromptRenderer
{
    public const string FormatV1 = "flc-prompt/v1";
    public const string Cs = "<|cs|>", Path = "<|path|>", Sem = "<|sem|>", Code = "<|code|>", Complete = "<|complete|>", Eol = "<|eol|>";
    public static readonly string[] SpecialTokens = [Cs, Path, Sem, Code, Complete, Eol, "<|end_completion|>", "<|eos|>"];

    public static TrainingRecord Render(FlcSampleRecord s, SemanticRecord? sem, PromptOptions o)
    {
        var sb = new StringBuilder();
        sb.Append(Cs);
        if (o.IncludePath) sb.Append(Path).Append(s.RelativePath);
        sb.Append('\n');

        var (semText, dropped) = sem is { Status: SemanticStatus.Resolved or SemanticStatus.PartiallyResolved }
            ? RenderSemantic(sem, o) : ("", 0);
        if (semText.Length > 0) sb.Append(Sem).Append('\n').Append(semText);

        // Code window: most recent text, cut on a line boundary from the left (never on the caret side).
        var left = s.LeftContext;
        bool truncated = s.LeftContextTruncated;
        if (left.Length > o.MaxCodeChars)
        {
            var cut = left.Length - o.MaxCodeChars;
            var nl = left.IndexOf('\n', cut);
            cut = nl >= 0 && nl < left.Length - 1 ? nl + 1 : cut;
            if (cut < left.Length && char.IsLowSurrogate(left[cut])) cut++;
            left = left[cut..];
            truncated = true;
        }
        sb.Append(Code).Append('\n').Append(left).Append(Complete);

        return new TrainingRecord
        {
            SampleId = s.SampleId, Split = s.Split, PromptFormat = o.Format, CaretKind = s.CaretKind, SemanticStatus = s.SemanticStatus,
            HasSemantic = semText.Length > 0, Prompt = sb.ToString(), Completion = s.TargetText + Eol,
            CodeChars = left.Length, CodeTruncated = truncated, SemanticChars = semText.Length, SemanticItemsDropped = dropped,
        };
    }

    /// <summary>
    /// Semantic block with a hard char budget. Lines are admitted by priority (EXPECT, RECV, CALL, ARG, LOCAL, RET, MEMBER,
    /// FIELD/PROPERTY, METHOD); list items are trimmed from the end. Output order is canonical regardless of priority.
    /// </summary>
    public static (string Text, int Dropped) RenderSemantic(SemanticRecord r, PromptOptions o)
    {
        var lines = new List<(int Order, int Priority, string Key, List<string> Items, string Sep)>();
        void Add(int order, int prio, string key, IEnumerable<string> items, string sep = " ")
        {
            var list = items.Where(x => !string.IsNullOrEmpty(x)).ToList();
            if (list.Count > 0) lines.Add((order, prio, key, list, sep));
        }

        Add(0, 5, "RET", r.ReturnType is null ? [] : [r.ReturnType]);
        Add(1, 0, "EXPECT", r.ExpectedType is null ? [] : [r.ExpectedType]);
        Add(2, 3, "ARG", r.Parameters.Select(p => $"{p.Name}:{p.Type}"));
        Add(3, 4, "LOCAL", r.Locals.Select(l => l.Type is null ? l.Name : $"{l.Name}:{l.Type}"));
        Add(4, 7, "FIELD", r.ThisMembers.Where(m => m.Kind is "field" or "const" or "event").Select(m => $"{m.Name}:{m.Type}"));
        Add(5, 7, "PROPERTY", r.ThisMembers.Where(m => m.Kind == "property").Select(m => $"{m.Name}:{m.Type}"));
        Add(6, 8, "METHOD", r.ThisMembers.Where(m => m.Kind == "method").Select(Compact), "; ");
        Add(7, 1, "RECV", r.ReceiverType is null ? [] : [r.ReceiverKind is "type" or "namespace" ? $"{r.ReceiverKind} {r.ReceiverType}" : r.ReceiverType]);
        Add(8, 6, "MEMBER", r.Members.Select(Compact), "; ");
        Add(9, 2, "CALL", r.InvocationCandidates.Select(c => CompactSig(c.Signature) + $" @{c.ArgumentIndex}" + (c.ParameterName is null ? "" : $" {c.ParameterName}:{c.ParameterType}")), " | ");

        int budget = o.MaxSemanticChars, dropped = 0;
        var admitted = new List<(int Order, string Text)>();
        foreach (var l in lines.OrderBy(l => l.Priority))
        {
            var items = l.Items.Take(o.MaxItemsPerLine).ToList();
            dropped += l.Items.Count - items.Count;
            string Text() => l.Key + " " + string.Join(l.Sep, items) + "\n";
            while (items.Count > 0 && Text().Length > budget) { items.RemoveAt(items.Count - 1); dropped++; }
            if (items.Count == 0) continue;
            var t = Text();
            budget -= t.Length;
            admitted.Add((l.Order, t));
        }
        return (string.Concat(admitted.OrderBy(a => a.Order).Select(a => a.Text)), dropped);
    }

    static string Compact(SymbolFact f) => f.Kind switch
    {
        "method" => CompactSig(f.Signature ?? f.Name) + (f.Overloads > 1 ? $" +{f.Overloads - 1}" : ""),
        "type" or "namespace" => f.Name,
        _ => $"{f.Name}:{f.Type}",
    };

    /// <summary>"Task&lt;Order?&gt; GetByIdAsync(Guid id)" -> "GetByIdAsync(Guid id)->Task&lt;Order?&gt;". Constructors and unparsable input pass through.</summary>
    public static string CompactSig(string sig)
    {
        int paren = sig.IndexOf('(');
        if (paren < 0) return sig;
        int depth = 0, split = -1;
        for (int i = paren - 1; i >= 0; i--)
        {
            char c = sig[i];
            if (c == '>') depth++;
            else if (c == '<') depth--;
            else if (c == ' ' && depth == 0) { split = i; break; }
        }
        if (split < 0) return sig;
        return sig[(split + 1)..] + "->" + sig[..split];
    }
}
