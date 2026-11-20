using System.Text.RegularExpressions;

namespace Fyrd.Core.Hub;

/// <summary>
/// The front matter of a seed file (<c>skills/*/SKILL.md</c>: personas, Controller request skills and session skills): simple single-line
/// <c>key: value</c> pairs between two <c>---</c> lines, then the body. A value may be a double-quoted YAML scalar
/// (the quotes are dropped, <c>\"</c> and <c>\\</c> are unescaped), which is how a value holding <c>": "</c> or
/// <c>" #"</c> stays valid YAML. Folded (<c>&gt;</c>) and multi-line values are not supported.
/// </summary>
public static partial class FrontMatter
{
    [GeneratedRegex(@"\A---[ \t]*\r?\n(?<fm>.*?)\r?\n---[ \t]*\r?\n?(?<body>.*)\z", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    /// <summary>
    /// False when the text doesn't open with front matter. <paramref name="fields"/> holds each key's value, trimmed and
    /// unquoted (a key given twice keeps its last value; a line without a colon is skipped); <paramref name="body"/> is
    /// what follows, with line ends as <c>\n</c> and trimmed.
    /// </summary>
    public static bool TryParse(string text, out IReadOnlyDictionary<string, string> fields, out string body)
    {
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        fields = parsed;
        body = "";
        var m = Pattern().Match(text);
        if (!m.Success) return false;
        foreach (var line in m.Groups["fm"].Value.Split('\n'))
        {
            var i = line.IndexOf(':');
            if (i < 0) continue;
            parsed[line[..i].Trim()] = Unquote(line[(i + 1)..].Trim());
        }
        body = m.Groups["body"].Value.Replace("\r\n", "\n").Trim();
        return true;
    }

    /// <summary>The value without one pair of surrounding double quotes; anything else is returned as it is.</summary>
    public static string Unquote(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"') return value;
        var sb = new System.Text.StringBuilder(value.Length);
        for (var i = 1; i < value.Length - 1; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length - 1 && value[i + 1] is '"' or '\\') i++;
            sb.Append(value[i]);
        }
        return sb.ToString();
    }
}
