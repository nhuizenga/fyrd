namespace Fyrd.Core.Hub;

/// <summary>
/// The text of one persona as another session's subagent. <c>For</c> names the coordinator role the variant is for when
/// the persona has more than one (the reviewer), else null. <c>Slim</c> is false when the cut fell back to the whole persona.
/// </summary>
public sealed record SubagentPersonaText(string Id, string? For, string Text, bool Slim);

/// <summary>
/// Cuts the subagent variant of a persona out of its stored definition. A Builder, Researcher, Validator or Monitor runs
/// the validator, reviewer and investigator personas as subagents in its own session and pastes this text into the
/// subagent's brief, so it holds only what a subagent can use: the persona's opening paragraph, the sections that
/// describe the work and its findings format, the rule that the work changes and posts nothing, and the Inputs and
/// Untrusted content rules of Working in Fyrd. Everything about coordinating (Your job, Persona subagents, Workers),
/// about who is talking and about memory stays out: a subagent has no workers, no owner turn and no memory of its own.
/// <para>
/// Each variant is a keep-list, applied to the definition by its <c># </c> headings, so the store, the console editor
/// and the seed hashes keep working on whole personas. When the definition lacks something the list needs (the owner
/// renamed a heading in the console), the variant is the whole definition: a longer brief, never a missing rule. That
/// fallback, and the "say which and ask" in the Inputs rule, are why the coordinators still follow the pasted text with
/// a line telling the subagent to ignore any instruction to ask the owner or post.
/// </para>
/// </summary>
public static class SubagentPersonas
{
    private const string WorkingInFyrd = "Working in Fyrd";

    private abstract record Keep;

    /// <summary>The text before the first heading.</summary>
    private sealed record Intro : Keep;

    /// <summary>A whole section, with its heading.</summary>
    private sealed record Section(string Heading) : Keep;

    /// <summary>One paragraph of a section, without the section's heading.</summary>
    private sealed record Paragraph(string Heading, string StartsWith) : Keep;

    /// <summary>A section's heading with only the named bullets. A missing optional bullet is left out.</summary>
    private sealed record Bullets(string Heading, string[] Required, string[] Optional) : Keep;

    private sealed record Variant(string Id, string? For, Keep[] Keeps);

    // Every persona has the Inputs rule; only some have an Untrusted content rule.
    private static readonly Keep Rules = new Bullets(WorkingInFyrd, ["Inputs"], ["Untrusted content"]);

    private static readonly Variant[] Variants =
    [
        new("validator", null, [new Intro(), new Section("The check"), Rules]),
        // The rule that a review changes and posts nothing closes the reviewer's Reviewing a plan section but covers both kinds of review.
        new("reviewer", "builder", [new Intro(), new Section("Reviewing code"), new Paragraph("Reviewing a plan", "Whoever does the reviewing"), Rules]),
        new("reviewer", "researcher", [new Intro(), new Section("Reviewing a plan"), Rules]),
        new("investigator", null, [new Intro(), new Section("The investigation"), new Section("As a subagent"), Rules]),
    ];

    /// <summary>The subagent variants of the personas present, in a fixed order: validator, reviewer (builder, researcher), investigator.</summary>
    public static IReadOnlyList<SubagentPersonaText> All(IReadOnlyList<HubPersona> personas)
    {
        var all = new List<SubagentPersonaText>();
        foreach (var v in Variants)
        {
            var persona = personas.FirstOrDefault(p => p.Id == v.Id);
            if (persona is null) continue;
            var slim = Cut(persona.Definition ?? "", v.Keeps);
            all.Add(new SubagentPersonaText(v.Id, v.For, slim ?? persona.Definition ?? "", slim is not null));
        }
        return all;
    }

    /// <summary>Null when the definition lacks a part the keep-list requires.</summary>
    private static string? Cut(string definition, Keep[] keeps)
    {
        var (intro, sections) = Split(definition);
        var parts = new List<string>();
        foreach (var keep in keeps)
        {
            string? part = keep switch
            {
                Intro => intro.Length > 0 ? intro : null,
                Section s => sections.TryGetValue(s.Heading, out var body) ? Titled(s.Heading, body) : null,
                Paragraph p => sections.TryGetValue(p.Heading, out var body)
                    ? Paragraphs(body).FirstOrDefault(x => x.StartsWith(p.StartsWith, StringComparison.Ordinal))
                    : null,
                Bullets b => sections.TryGetValue(b.Heading, out var body) ? Pick(b, body) : null,
                _ => null,
            };
            if (part is null) return null;
            parts.Add(part);
        }
        return string.Join("\n\n", parts);
    }

    private static string Titled(string heading, string body) => body.Length > 0 ? $"# {heading}\n\n{body}" : $"# {heading}";

    private static string? Pick(Bullets keep, string body)
    {
        var bullets = BulletList(body);
        var kept = new List<string>();
        foreach (var label in keep.Required)
        {
            var bullet = bullets.FirstOrDefault(b => HasLabel(b, label));
            if (bullet is null) return null;
            kept.Add(bullet);
        }
        foreach (var label in keep.Optional)
        {
            var bullet = bullets.FirstOrDefault(b => HasLabel(b, label));
            if (bullet is not null) kept.Add(bullet);
        }
        return Titled(keep.Heading, string.Join("\n", kept));
    }

    /// <summary>A bullet is labelled <c>- **Label**</c> or <c>- **Label.**</c>.</summary>
    private static bool HasLabel(string bullet, string label) =>
        bullet.StartsWith($"- **{label}**", StringComparison.Ordinal) || bullet.StartsWith($"- **{label}.**", StringComparison.Ordinal);

    /// <summary>The top-level bullets of a section body, each with its continuation lines.</summary>
    private static List<string> BulletList(string body)
    {
        var bullets = new List<string>();
        List<string>? current = null;
        foreach (var line in body.Split('\n'))
        {
            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                Flush();
                current = [line];
            }
            else if (current is not null && line.Length > 0 && char.IsWhiteSpace(line[0]) && line.Trim().Length > 0)
                current.Add(line);
            else
                Flush();
        }
        Flush();
        return bullets;

        void Flush()
        {
            if (current is not null) bullets.Add(string.Join("\n", current).TrimEnd());
            current = null;
        }
    }

    private static IEnumerable<string> Paragraphs(string body) =>
        body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0);

    /// <summary>
    /// Splits a definition at its level-one headings (see <see cref="PersonaHeadings"/>). A heading that appears twice
    /// keeps its first section.
    /// </summary>
    private static (string Intro, Dictionary<string, string> Sections) Split(string definition)
    {
        var lines = PersonaHeadings.Lines(definition);
        var headings = PersonaHeadings.Find(lines);
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        var intro = Text(0, headings.Count > 0 ? headings[0].Line : lines.Length);
        for (var i = 0; i < headings.Count; i++)
            sections.TryAdd(headings[i].Heading, Text(headings[i].Line + 1, i + 1 < headings.Count ? headings[i + 1].Line : lines.Length));
        return (intro, sections);

        string Text(int from, int to) => string.Join("\n", lines[from..to]).Trim();
    }
}
