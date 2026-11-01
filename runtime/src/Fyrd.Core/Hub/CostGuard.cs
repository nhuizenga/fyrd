using System.Globalization;
using Fyrd.Core.CloudSessions;
using Fyrd.Core.Config;

namespace Fyrd.Core.Hub;

/// <summary>What <see cref="CostGuard.RunAsync"/> found: a cost view per live session, the fyrd budget, and the first failed action.</summary>
public sealed record CostPass(IReadOnlyDictionary<string, HubCostView> Views, HubBudgetView Budget, string? Error);

/// <summary>
/// Applies the daily spend limits on each hub poll. A session at its limit is blocked, interrupted once and the
/// Controller is told once; the fyrd at its limit blocks every session but the Controller (no interrupts) and the
/// Controller is told once. The Controller itself is never blocked or interrupted.
/// </summary>
public sealed class CostGuard(CostLedger ledger, ConfigStore config, ICloudSessions cloud, ControllerManager controller)
{
    public const decimal ExtendThreshold = 0.9m;
    public static readonly IReadOnlyList<decimal> ExtendAmounts = [25m, 100m];

    /// <summary>Computes the views only (no interrupts, no messages, nothing saved).</summary>
    public (Dictionary<string, HubCostView> Views, HubBudgetView Budget) Build(IReadOnlyCollection<string> liveIds)
    {
        var cfg = config.Load();
        var controllerId = controller.CurrentId;
        var live = liveIds.ToHashSet(StringComparer.Ordinal);
        var costs = live.ToDictionary(id => id, ledger.TodayCost, StringComparer.Ordinal);
        var total = costs.Values.Sum(c => c.Cost) + ledger.ArchivedToday(live);
        var fyrdBlocked = total >= cfg.FyrdDailyLimitUsd;

        var views = new Dictionary<string, HubCostView>(StringComparer.Ordinal);
        foreach (var (id, c) in costs)
        {
            if (!c.HasData) continue;
            var exempt = id == controllerId;
            var limit = cfg.SessionDailyLimitUsd + ledger.ExtraUsd(id);
            var ratio = (double)(c.Cost / limit);
            var blocked = exempt ? null
                : c.Cost >= limit ? "session"
                : fyrdBlocked ? "fyrd"
                : null;
            views[id] = new HubCostView(c.Cost, limit, ratio, c.Estimated, blocked, !exempt && c.Cost >= limit * ExtendThreshold, exempt);
        }
        return (views, new HubBudgetView(
            ledger.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ledger.TimeZone.Id, total, cfg.FyrdDailyLimitUsd, fyrdBlocked));
    }

    /// <param name="live">Every session the poll lists, with its title.</param>
    /// <param name="controllerLive">Whether the Controller was read as live this pass, so a message can reach it.</param>
    public async Task<CostPass> RunAsync(IReadOnlyList<(string Id, string Title)> live, bool controllerLive, CancellationToken ct)
    {
        var (views, budget) = Build(live.Select(l => l.Id).ToList());
        foreach (var (id, title) in live)
            if (views.TryGetValue(id, out var v)) ledger.Record(id, title, v.CostToday);

        var cfg = config.Load();
        var controllerId = controller.CurrentId;
        string? error = null;

        foreach (var (id, title) in live)
        {
            if (!views.TryGetValue(id, out var v) || v.Exempt || v.Blocked != "session") continue;
            if (ledger.NotifiedAtLimit(id) == v.LimitUsd) continue;
            try
            {
                await cloud.InterruptAsync(id, ct);
                if (controllerLive && controllerId is not null)
                    await cloud.SendMessageAsync(controllerId,
                        $"Fyrd budget: session {title} ({id}) reached its {Usd(v.LimitUsd)} USD daily limit ({Usd(v.CostToday)} USD spent) " +
                        "and was interrupted. Don't send it work today unless the owner raises its limit.", ct);
                ledger.SetNotified(id, v.LimitUsd);
            }
            catch (Exception e) when (IsRetryable(e, ct))
            {
                error ??= $"budget {id}: {e.Message}";
            }
        }

        if (budget.FyrdBlocked && controllerLive && controllerId is not null && ledger.FyrdNotifiedAtLimit != cfg.FyrdDailyLimitUsd)
        {
            try
            {
                await cloud.SendMessageAsync(controllerId,
                    $"Fyrd budget: the fyrd reached its {Usd(cfg.FyrdDailyLimitUsd)} USD daily limit ({Usd(budget.FyrdCostToday)} USD spent). " +
                    "Console sending is paused for every session except you until midnight. Running work was not interrupted. " +
                    "Don't start new peer work today unless the owner raises the limit.", ct);
                ledger.SetFyrdNotified(cfg.FyrdDailyLimitUsd);
            }
            catch (Exception e) when (IsRetryable(e, ct))
            {
                error ??= $"budget fyrd: {e.Message}";
            }
        }

        ledger.Flush();
        return new CostPass(views, budget, error);
    }

    private static string Usd(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Auth failures and cancellation end the pass; anything else is reported and retried on the next pass.</summary>
    private static bool IsRetryable(Exception e, CancellationToken ct) =>
        e is not (CloudSessionAuthException or ClaudeAuthMissingException)
        && (e is not OperationCanceledException || !ct.IsCancellationRequested);
}
