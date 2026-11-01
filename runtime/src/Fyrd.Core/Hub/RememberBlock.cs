using System.Text.RegularExpressions;

namespace Fyrd.Core.Hub;

/// <summary>Scope is "shared" or "role".</summary>
public sealed record RememberDecl(string Scope, string Text);

/// <summary>Parses fenced <c>fyrd-remember</c> blocks (plain-text body, optional first line <c>scope: shared|role</c>).</summary>
public static partial class RememberBlock
{
    private const int MaxTextLength = 2000;

    [GeneratedRegex(
        @"^[ \t]*(?<fence>`{3,})fyrd-remember[ \t]*\r?\n(?<body>.*?)\r?\n[ \t]*\k<fence>[ \t]*\r?$",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"\A[ \t]*scope[ \t]*:[ \t]*(?<scope>[^\r\n]*?)[ \t]*(?:\r?\n|\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScopeLineRegex();

    public static IReadOnlyList<RememberDecl> Parse(string assistantText)
    {
        var result = new List<RememberDecl>();
        foreach (Match m in FenceRegex().Matches(assistantText ?? ""))
        {
            var body = m.Groups["body"].Value;
            var scope = "role";
            var sm = ScopeLineRegex().Match(body);
            if (sm.Success)
            {
                scope = sm.Groups["scope"].Value.ToLowerInvariant();
                body = body[sm.Length..];
            }
            if (scope is not ("shared" or "role")) continue;
            var text = body.Trim();
            if (text.Length == 0 || text.Length > MaxTextLength) continue;
            result.Add(new RememberDecl(scope, text));
        }
        return result;
    }
}
