using System.Text.RegularExpressions;
using Fyrd.Core.Config;
using Fyrd.Core.Specs;

namespace Fyrd.Core.Orchestration;

public sealed record Personas(string Lead, string Implementer, string Reviewer, string Validator)
{
    public static Personas Load(string dir)
    {
        static string Read(string dir, string name)
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) throw new FileNotFoundException($"Persona file not found: {name}", path);
            return File.ReadAllText(path);
        }

        return new Personas(Read(dir, "lead.md"), Read(dir, "implementer.md"),
            Read(dir, "reviewer.md"), Read(dir, "validator.md"));
    }
}

public static partial class LeadPrompt
{
    public static string Build(SpecRun run, string specMarkdown, string memory, FyrdConfig config, Personas personas)
    {
        var environmentId = config.EnvironmentId
            ?? throw new InvalidOperationException("EnvironmentId must be configured before launching a lead.");
        var values = new Dictionary<string, string>
        {
            ["SPEC_ID"] = run.Id,
            ["REPO"] = run.Repo,
            ["SPEC"] = specMarkdown,
            ["MEMORY"] = string.IsNullOrWhiteSpace(memory) ? "(none yet)" : memory,
            ["SQUAD_CAP"] = config.SquadCap.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["PEER_MODEL"] = config.PeerModel,
            ["ENVIRONMENT_ID"] = environmentId,
            ["IMPLEMENTER_PERSONA"] = personas.Implementer,
            ["REVIEWER_PERSONA"] = personas.Reviewer,
            ["VALIDATOR_PERSONA"] = personas.Validator,
        };
        // Single pass so injected text (spec, memory, personas) is never re-expanded.
        return Placeholder().Replace(personas.Lead, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
    }

    [GeneratedRegex(@"\{\{([A-Z_]+)\}\}")]
    private static partial Regex Placeholder();
}
