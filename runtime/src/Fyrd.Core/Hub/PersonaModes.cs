namespace Fyrd.Core.Hub;

/// <summary>
/// One mode of a persona whose session works in a single mode for its whole life. <c>Omit</c> is the heading of the
/// section such a session never uses, <c>Until</c> the heading of the section that follows it, <c>Instead</c> the text
/// that takes the omitted section's place (empty when nothing does) and <c>Text</c> the resulting persona.
/// </summary>
public sealed record PersonaModeVariant(string Id, string Mode, string Omit, string Until, string Instead, string Text);

/// <summary>
/// Works out the mode variants of the monitor and reviewer personas from their stored definitions. A Monitor watches a
/// pull request or a Slack channel, and a Reviewer started for one pull request, branch or plan reviews code or a plan,
/// so the section for the other mode is context the session pays for on every turn and never uses.
/// <para>
/// A variant is an omit-list, not a keep-list: the whole persona minus one section. A section the owner adds in
/// Settings therefore stays in every variant; a session that coordinates must never lose a rule because a list did not
/// name it. Where the omitted section holds a paragraph that binds both modes, that paragraph takes the section's place.
/// </para>
/// <para>
/// The Controller message does not carry <c>Text</c>. It carries the whole persona once and, per variant, a small block
/// naming the section to leave out, the heading it ends at and the text to put there, and the Controller applies that
/// when it copies the persona into a session's prompt (see the Controller design spec, Delivery, for why). <c>Text</c>
/// is what a correct copy looks like; the tests measure it. Like the subagent cuts, a variant is worked out on read, so
/// the store, the console editor and the seed hashes keep working on whole personas. When the definition lacks
/// something a variant needs (a heading renamed in Settings, the shared paragraph reworded), there is no variant and
/// the Controller uses the whole persona.
/// </para>
/// </summary>
public static class PersonaModes
{
    /// <summary><c>Needs</c> is the section of the mode itself; <c>Shared</c> starts the paragraph of the omitted section that binds both modes.</summary>
    private sealed record Mode(string Id, string Name, string Omit, string Needs, string? Shared);

    private static readonly Mode[] Modes =
    [
        // The last paragraph of Slack mode (start a worker only when there is work) is about every Monitor.
        new("monitor", "pr", Omit: "Slack mode", Needs: "PR mode", Shared: "Start a worker only when there is work"),
        new("monitor", "slack", Omit: "PR mode", Needs: "Slack mode", Shared: null),
        // The rule that a review changes and posts nothing closes Reviewing a plan but covers both kinds of review.
        new("reviewer", "code", Omit: "Reviewing a plan", Needs: "Reviewing code", Shared: "Whoever does the reviewing"),
        new("reviewer", "plan", Omit: "Reviewing code", Needs: "Reviewing a plan", Shared: null),
    ];

    /// <summary>The variants that can be cut from the personas present, in a fixed order: monitor (pr, slack), reviewer (code, plan).</summary>
    public static IReadOnlyList<PersonaModeVariant> All(IReadOnlyList<HubPersona> personas)
    {
        var all = new List<PersonaModeVariant>();
        foreach (var mode in Modes)
        {
            var persona = personas.FirstOrDefault(p => p.Id == mode.Id);
            if (persona is not null && Cut(persona.Definition ?? "", mode) is { } variant) all.Add(variant);
        }
        return all;
    }

    /// <summary>Null when the definition doesn't have the shape the variant needs.</summary>
    private static PersonaModeVariant? Cut(string definition, Mode mode)
    {
        var lines = PersonaHeadings.Lines(definition);
        var headings = PersonaHeadings.Find(lines);
        // Each heading exactly once: with a repeated heading, "the section headed X" would not name one place.
        if (headings.Count(h => h.Heading == mode.Omit) != 1 || headings.Count(h => h.Heading == mode.Needs) != 1) return null;
        var at = headings.FindIndex(h => h.Heading == mode.Omit);
        // The omitted section must end at a heading, so the instruction can name where the copy picks up again.
        if (at + 1 >= headings.Count) return null;
        var (start, end) = (headings[at].Line, headings[at + 1].Line);
        var until = headings[at + 1].Heading;
        if (headings.Count(h => h.Heading == until) != 1) return null;

        var instead = "";
        if (mode.Shared is not null)
        {
            var body = string.Join("\n", lines[(start + 1)..end]);
            var shared = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim())
                .FirstOrDefault(p => p.StartsWith(mode.Shared, StringComparison.Ordinal));
            if (shared is null) return null;
            instead = shared;
        }

        var before = string.Join("\n", lines[..start]).TrimEnd();
        var after = string.Join("\n", lines[end..]).TrimEnd();
        var parts = new[] { before, instead, after }.Where(p => p.Length > 0);
        return new PersonaModeVariant(mode.Id, mode.Name, mode.Omit, until, instead, string.Join("\n\n", parts));
    }
}
