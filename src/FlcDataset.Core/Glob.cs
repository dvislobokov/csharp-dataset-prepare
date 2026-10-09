using System.Text;
using System.Text.RegularExpressions;

namespace FlcDataset.Core;

/// <summary>Minimal glob matcher over '/'-separated relative paths: '**' (any dirs), '*' (within segment), '?'.</summary>
public sealed class Glob
{
    readonly Regex _regex;
    public string Pattern { get; }

    public Glob(string pattern)
    {
        Pattern = pattern;
        var sb = new StringBuilder("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                bool slash = i + 2 < pattern.Length && pattern[i + 2] == '/';
                sb.Append(slash ? "(?:.*/)?" : ".*");
                i += slash ? 2 : 1;
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append('$');
        _regex = new Regex(sb.ToString(), RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }

    public bool IsMatch(string relativePath) => _regex.IsMatch(relativePath);

    public static string? FirstMatch(IEnumerable<Glob> globs, string path) =>
        globs.FirstOrDefault(g => g.IsMatch(path))?.Pattern;
}
