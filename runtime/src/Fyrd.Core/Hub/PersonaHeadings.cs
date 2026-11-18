namespace Fyrd.Core.Hub;

/// <summary>
/// Finds the sections of a persona definition. A section starts at a level-one heading: a line starting <c># </c>
/// outside a code fence. <see cref="SubagentPersonas"/> and <see cref="PersonaModes"/> both cut personas by these.
/// </summary>
internal static class PersonaHeadings
{
    /// <summary>The definition's lines, with CRLF read as LF.</summary>
    public static string[] Lines(string definition) => (definition ?? "").Replace("\r\n", "\n").Split('\n');

    /// <summary>Each heading line's index and its heading text (without the <c># </c>), in order.</summary>
    public static List<(int Line, string Heading)> Find(string[] lines)
    {
        var headings = new List<(int, string)>();
        var fenced = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal)) fenced = !fenced;
            if (!fenced && lines[i].StartsWith("# ", StringComparison.Ordinal)) headings.Add((i, lines[i][2..].Trim()));
        }
        return headings;
    }
}
