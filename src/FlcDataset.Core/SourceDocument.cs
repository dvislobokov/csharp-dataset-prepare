using System.Text;

namespace FlcDataset.Core;

public enum EndOfLine { Lf, Crlf, Cr, Other, None }

/// <summary>One physical line. Offsets are UTF-16 code units into <see cref="SourceDocument.Text"/>.</summary>
public readonly record struct LineInfo(int Number, int Start, int End, int EndIncludingBreak, EndOfLine Break);

/// <summary>
/// Exact, decoded view of a source file. <see cref="Text"/> excludes the UTF-8 BOM (Roslyn-compatible);
/// original bytes are recoverable as BOM + UTF8(Text). Line breaks follow Roslyn rules:
/// CRLF, CR, LF, U+0085, U+2028, U+2029.
/// </summary>
public sealed class SourceDocument
{
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    public const int BomLength = 3;

    public string Text { get; }
    public bool HasBom { get; }
    public string Sha256 { get; }
    public long ByteLength { get; }
    public IReadOnlyList<LineInfo> Lines { get; }

    SourceDocument(string text, bool hasBom, string sha, long byteLength)
    {
        Text = text;
        HasBom = hasBom;
        Sha256 = sha;
        ByteLength = byteLength;
        Lines = ComputeLines(text);
    }

    /// <summary>Decode strictly as UTF-8. Returns null with a reason when bytes are not valid UTF-8.</summary>
    public static SourceDocument? TryDecode(byte[] bytes, out string? failureReason)
    {
        failureReason = null;
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        if (!bom && bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            failureReason = "utf16_encoding";
            return null;
        }
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        }
        catch (DecoderFallbackException)
        {
            failureReason = "invalid_utf8";
            return null;
        }
        if (text.Contains('\0'))
        {
            failureReason = "binary_nul";
            return null;
        }
        return new SourceDocument(text, bom, Hashing.Sha256Hex(bytes), bytes.Length);
    }

    public static SourceDocument FromText(string text, bool hasBom = false)
    {
        var bytes = ToBytes(text, hasBom);
        return new SourceDocument(text, hasBom, Hashing.Sha256Hex(bytes), bytes.Length);
    }

    public static byte[] ToBytes(string text, bool hasBom)
    {
        var body = StrictUtf8.GetBytes(text);
        if (!hasBom) return body;
        var all = new byte[body.Length + 3];
        all[0] = 0xEF; all[1] = 0xBB; all[2] = 0xBF;
        body.CopyTo(all, 3);
        return all;
    }

    /// <summary>Byte offset in the original file (BOM included) for a UTF-16 offset in <see cref="Text"/>.</summary>
    public long ByteOffset(int utf16Offset)
    {
        var starts = _lineByteStarts ??= ComputeLineByteStarts();
        var line = LineIndexOf(utf16Offset);
        return starts[line] + StrictUtf8.GetByteCount(Text.AsSpan(Lines[line].Start, utf16Offset - Lines[line].Start));
    }

    long[]? _lineByteStarts;

    long[] ComputeLineByteStarts()
    {
        var starts = new long[Lines.Count];
        long acc = HasBom ? BomLength : 0;
        for (int i = 0; i < Lines.Count; i++)
        {
            starts[i] = acc;
            acc += StrictUtf8.GetByteCount(Text.AsSpan(Lines[i].Start, Lines[i].EndIncludingBreak - Lines[i].Start));
        }
        return starts;
    }

    public int LineIndexOf(int offset)
    {
        int lo = 0, hi = Lines.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (Lines[mid].Start <= offset) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    public (int Line, int Column) LineColumn(int offset)
    {
        var line = LineIndexOf(offset);
        return (line, offset - Lines[line].Start);
    }

    public int OffsetOf(int line, int column) => Lines[line].Start + column;

    public static bool IsLineBreakChar(char c) => c is '\r' or '\n' or '\u0085' or '\u2028' or '\u2029';

    static List<LineInfo> ComputeLines(string text)
    {
        var lines = new List<LineInfo>();
        int start = 0, i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (!IsLineBreakChar(c)) { i++; continue; }
            EndOfLine kind;
            int breakLen = 1;
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { kind = EndOfLine.Crlf; breakLen = 2; }
            else kind = c switch { '\r' => EndOfLine.Cr, '\n' => EndOfLine.Lf, _ => EndOfLine.Other };
            lines.Add(new LineInfo(lines.Count, start, i, i + breakLen, kind));
            i += breakLen;
            start = i;
        }
        lines.Add(new LineInfo(lines.Count, start, text.Length, text.Length, EndOfLine.None));
        return lines;
    }

    /// <summary>Dominant newline style for the file: lf, crlf, cr, mixed or none.</summary>
    public string NewlineStyle()
    {
        var kinds = Lines.Where(l => l.Break != EndOfLine.None).Select(l => l.Break).Distinct().ToList();
        return kinds.Count switch { 0 => "none", 1 => kinds[0].ToString().ToLowerInvariant(), _ => "mixed" };
    }
}
