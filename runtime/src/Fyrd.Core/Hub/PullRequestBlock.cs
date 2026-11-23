using System.Globalization;
using System.Text.RegularExpressions;

namespace Fyrd.Core.Hub;

/// <summary>
/// Parses fenced <c>fyrd-prs</c> blocks: one pull request URL per line, which a session emits (the <c>fyrd:checkouts</c>
/// skill) to say which pull requests it works on. The text is model output, so it is untrusted: a line counts only when
/// it is exactly <c>https://github.com/&lt;owner&gt;/&lt;repo&gt;/pull/&lt;number&gt;</c> (surrounding spaces and an
/// upper-case scheme or host aside); any other line is ignored. URLs come back normalised (all lower case, see
/// <see cref="PullRequestUrl.Canonical"/>, and no leading zeros), each one once, at most <see cref="MaxPerBlock"/> per block, and every one passes
/// <see cref="PullRequestUrl.IsValid"/>.
/// </summary>
public static partial class PullRequestBlock
{
    /// <summary>The most URLs taken from one block; later lines are ignored.</summary>
    public const int MaxPerBlock = 20;

    /// <summary>A line longer than this is no URL (GitHub caps an owner at 39 characters and a repo at 100).</summary>
    private const int MaxLineLength = 200;

    [GeneratedRegex(
        @"^[ \t]*(?<fence>`{3,})fyrd-prs[ \t]*\r?\n(?<body>.*?)\r?\n?[ \t]*\k<fence>[ \t]*\r?$",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex FenceRegex();

    // GitHub's own rules: an owner is alphanumerics and single inner hyphens, at most 39 characters; a repo is letters,
    // digits, '.', '_' and '-', at most 100. A number is 1 or more, with no leading zero (normalised below).
    [GeneratedRegex(
        @"\A(?i:https://github\.com)/(?<owner>[A-Za-z0-9](?:-?[A-Za-z0-9]){0,38})/(?<repo>[A-Za-z0-9._-]{1,100})/pull/(?<number>[0-9]{1,10})\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    public static IReadOnlyList<string> Parse(string? assistantText)
    {
        var all = new List<string>();
        foreach (Match m in FenceRegex().Matches(assistantText ?? ""))
        {
            var block = new List<string>();
            foreach (var raw in m.Groups["body"].Value.Split('\n'))
            {
                if (block.Count == MaxPerBlock) break;
                if (Normalize(raw) is { } url && !block.Contains(url)) block.Add(url);
            }
            foreach (var url in block)
                if (!all.Contains(url)) all.Add(url);
        }
        return all;
    }

    /// <summary>The normalised URL a line holds, or null when the line is anything but one strictly formed PR URL.</summary>
    public static string? Normalize(string? line)
    {
        var text = (line ?? "").Trim(' ', '\t', '\r');
        if (text.Length is 0 or > MaxLineLength) return null;
        var m = UrlRegex().Match(text);
        if (!m.Success) return null;
        var repo = m.Groups["repo"].Value;
        if (repo is "." or "..") return null;
        if (!long.TryParse(m.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || number is < 1 or > int.MaxValue) return null;
        var url = PullRequestUrl.Canonical(
            $"https://github.com/{m.Groups["owner"].Value}/{repo}/pull/{number.ToString(CultureInfo.InvariantCulture)}");
        // The value that is stored and later queried is checked again, as built.
        return PullRequestUrl.IsValid(url) ? url : null;
    }
}
