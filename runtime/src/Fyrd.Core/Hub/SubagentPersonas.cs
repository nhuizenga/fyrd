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
/// renamed a heading or a bullet in the console, or moved or reworded the reviewer's rule that a review changes and
/// posts nothing), or has a heading the list keeps more than once, the variant is the whole definition: a longer
/// brief, never a missing rule. The Untrusted content
/// rule is required of the investigator, whose persona has one, and optional for the validator and the reviewer,
/// whose personas don't. That fallback, and the "say which and ask" in the Inputs rule, are why the coordinators still
/// follow the pasted text with a line telling the subagent to ignore any instruction to ask the owner or post.
/// </para>
/// <para>
/// A kept bullet is cut whole: it runs to the next top-level bullet or to an unindented line after a blank line. An
/// unindented line directly under it (a hard-wrapped bullet) is still part of it, and so is indented text after
/// blank lines. Code fences are read as CommonMark does (three or more backticks or tildes, closed only by a fence
/// of the same character that is at least as long), except that indentation is ignored: a fence indented four or
/// more spaces opens and closes one too. So a <c># </c> line inside a fence is not a heading. A persona whose
/// definition is empty has no variant.
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

    /// <summary>
    /// Adds no text: requires that a paragraph of what the variant has kept so far starts with this. Without one the
    /// variant is the whole definition.
    /// </summary>
    private sealed record Rule(string StartsWith) : Keep;

    /// <summary>A section's heading with only the named bullets. A missing optional bullet is left out.</summary>
    private sealed record Bullets(string Heading, string[] Required, string[] Optional) : Keep;

    private sealed record Variant(string Id, string? For, Keep[] Keeps);

    // Every persona has the Inputs rule. The validator and reviewer personas have no Untrusted content rule, so for
    // them it is kept when present; the investigator's has one, so there it is required like any other part.
    private static readonly Keep Rules = new Bullets(WorkingInFyrd, ["Inputs"], ["Untrusted content"]);
    private static readonly Keep InvestigatorRules = new Bullets(WorkingInFyrd, ["Inputs", "Untrusted content"], []);

    // The rule that a review changes and posts nothing closes the reviewer's Reviewing a plan section but covers both kinds of review.
    private const string ReviewChangesNothing = "Whoever does the reviewing";

    private static readonly Variant[] Variants =
    [
        new("validator", null, [new Intro(), new Section("The check"), Rules]),
        new("reviewer", "builder", [new Intro(), new Section("Reviewing code"), new Paragraph("Reviewing a plan", ReviewChangesNothing), Rules]),
        new("reviewer", "researcher", [new Intro(), new Section("Reviewing a plan"), new Rule(ReviewChangesNothing), Rules]),
        new("investigator", null, [new Intro(), new Section("The investigation"), new Section("As a subagent"), InvestigatorRules]),
    ];

    /// <summary>
    /// The subagent variants of the personas present, in a fixed order: validator, reviewer (builder, researcher),
    /// investigator. A persona that is missing or whose definition is empty has none.
    /// </summary>
    public static IReadOnlyList<SubagentPersonaText> All(IReadOnlyList<HubPersona> personas)
    {
        var all = new List<SubagentPersonaText>();
        foreach (var v in Variants)
        {
            var persona = personas.FirstOrDefault(p => p.Id == v.Id);
            if (persona is null || string.IsNullOrWhiteSpace(persona.Definition)) continue;
            var slim = Cut(persona.Definition, v.Keeps);
            all.Add(new SubagentPersonaText(v.Id, v.For, slim ?? persona.Definition, slim is not null));
        }
        return all;
    }

    /// <summary>Null when the definition lacks a part the keep-list requires, or has a heading the list keeps more than once.</summary>
    private static string? Cut(string definition, Keep[] keeps)
    {
        var (intro, sections, repeated) = Split(definition);
        var parts = new List<string>();
        foreach (var keep in keeps)
        {
            string? part = keep switch
            {
                Intro => intro.Length > 0 ? intro : null,
                Section s => Body(s.Heading) is { } body ? Titled(s.Heading, body) : null,
                Paragraph p => Body(p.Heading) is { } body
                    ? Paragraphs(body).FirstOrDefault(x => x.StartsWith(p.StartsWith, StringComparison.Ordinal))
                    : null,
                Rule r => parts.SelectMany(Paragraphs).Any(x => x.StartsWith(r.StartsWith, StringComparison.Ordinal)) ? "" : null,
                Bullets b => Body(b.Heading) is { } body ? Pick(b, body) : null,
                _ => null,
            };
            if (part is null) return null;
            if (part.Length > 0) parts.Add(part);
        }
        return string.Join("\n\n", parts);

        // A heading that appears twice has two sections and the keep-list names one: neither is cut.
        string? Body(string heading) => !repeated.Contains(heading) && sections.TryGetValue(heading, out var body) ? body : null;
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

    /// <summary>
    /// The top-level bullets of a section body, each with its continuation lines. An unindented line directly under
    /// a bullet's text continues it (a hard-wrapped bullet, or code at the left margin in a fence the bullet holds).
    /// A bullet stays open across blank lines while the next line with text is indented. The next top-level bullet
    /// (a line starting <c>- </c>) ends it, and so does an unindented line after a blank line. A body has no
    /// <c># </c> heading outside a code fence: <see cref="Split"/> ends the section there.
    /// </summary>
    private static List<string> BulletList(string body)
    {
        var bullets = new List<string>();
        List<string>? current = null;
        var blanks = new List<string>();
        foreach (var line in body.Split('\n'))
        {
            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                Flush();
                current = [line];
            }
            else if (line.Trim().Length == 0)
            {
                if (current is not null) blanks.Add(line);
            }
            else if (current is not null && char.IsWhiteSpace(line[0]))
            {
                current.AddRange(blanks);
                blanks.Clear();
                current.Add(line);
            }
            else if (current is not null && blanks.Count == 0)
                current.Add(line); // no blank line before it: the bullet's paragraph goes on
            else
                Flush();
        }
        Flush();
        return bullets;

        void Flush()
        {
            if (current is not null) bullets.Add(string.Join("\n", current).TrimEnd());
            current = null;
            blanks.Clear();
        }
    }

    private static IEnumerable<string> Paragraphs(string body) =>
        body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0);

    /// <summary>
    /// Splits a definition at its level-one headings (a line starting <c># </c> outside a code fence). A heading that
    /// appears twice is listed in <c>Repeated</c>, and <c>Sections</c> has its first section. A fence that is never
    /// closed runs to the end, so the headings after it are not sections and the variants that need them fall back
    /// to the whole definition.
    /// </summary>
    private static (string Intro, Dictionary<string, string> Sections, HashSet<string> Repeated) Split(string definition)
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        var repeated = new HashSet<string>(StringComparer.Ordinal);
        var intro = "";
        string? heading = null;
        var lines = new List<string>();
        (char Mark, int Length)? open = null;
        foreach (var line in definition.Replace("\r\n", "\n").Split('\n'))
        {
            var fence = Fence(line);
            if (open is { } o)
            {
                // Only a fence of the same character, at least as long and with nothing after it, closes.
                if (fence is { } f && f.Mark == o.Mark && f.Length >= o.Length && f.After.Trim().Length == 0) open = null;
            }
            else if (fence is { } f && (f.Mark == '~' || !f.After.Contains('`')))
                open = (f.Mark, f.Length); // a backtick fence has no backtick after its opening run
            else if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                Close();
                heading = line[2..].Trim();
                continue;
            }
            lines.Add(line);
        }
        Close();
        return (intro, sections, repeated);

        void Close()
        {
            var text = string.Join("\n", lines).Trim();
            lines.Clear();
            if (heading is null) intro = text;
            else if (!sections.TryAdd(heading, text)) repeated.Add(heading);
        }
    }

    /// <summary>
    /// The run of three or more backticks or tildes a line starts with after its indentation: the character, the
    /// run's length and what follows it. Null when the line starts with no such run. Any indentation is ignored, where
    /// CommonMark takes a line indented four or more spaces as no fence.
    /// </summary>
    private static (char Mark, int Length, string After)? Fence(string line)
    {
        var t = line.TrimStart();
        if (t.Length < 3 || (t[0] != '`' && t[0] != '~')) return null;
        var n = 0;
        while (n < t.Length && t[n] == t[0]) n++;
        return n >= 3 ? (t[0], n, t[n..]) : null;
    }
}
