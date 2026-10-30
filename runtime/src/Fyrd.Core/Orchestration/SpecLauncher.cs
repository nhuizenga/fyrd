using Fyrd.Core.CloudSessions;
using Fyrd.Core.Config;
using Fyrd.Core.Specs;

namespace Fyrd.Core.Orchestration;

/// <summary>Starts the lead cloud session for a spec the caller has already moved Draft to Queued.</summary>
public sealed class SpecLauncher(
    SpecStore specs, ConfigStore config, DecisionMemory memory, ICloudSessions cloud, Personas personas)
{
    public async Task<SpecRun> LaunchAsync(string specId, CancellationToken ct)
    {
        try
        {
            var run = specs.Get(specId) ?? throw new KeyNotFoundException($"Spec '{specId}' not found.");
            var cfg = config.Load();
            var environmentId = cfg.EnvironmentId
                ?? throw new InvalidOperationException("EnvironmentId must be configured before launching a lead.");
            var prompt = LeadPrompt.Build(run, specs.ReadMarkdown(specId), memory.ReadFor(run.Repo), cfg, personas);
            var session = await cloud.CreateAsync(new CreateSessionRequest(
                Title: $"Fyrd lead · {run.Title}",
                Tags: ["fyrd", $"fyrd-spec-{specId}", "fyrd-role-lead"],
                EnvironmentId: environmentId,
                FirstMessage: prompt,
                Model: cfg.LeadModel,
                AppendSystemPrompt: null,
                Source: new SessionSource($"https://github.com/{run.Repo}", null),
                PermissionMode: "auto"), ct);
            return specs.SetLead(specId, session.Id);
        }
        catch
        {
            specs.TryTransition(specId, SpecStatus.Queued, SpecStatus.Draft);
            throw;
        }
    }
}
