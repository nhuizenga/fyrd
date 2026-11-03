using System.Globalization;
using Fyrd.Core.CloudSessions;
using Fyrd.Core.Config;

namespace Fyrd.Core.Hub;

/// <summary>The cost view of every live session that has cost data, and the fyrd budget.</summary>
public sealed record CostPass(IReadOnlyDictionary<string, HubCostView> Views, HubBudgetView Budget);

/// <summary>
/// Applies the daily spend limits on each hub poll. A session at its limit is blocked, interrupted once and the
/// Controller is told once; the fyrd at its limit blocks every session but the Controller (no interrupts) and the
/// Controller is told once. The Controller itself is never blocked or interrupted.
/// </summary>
public sealed class CostGuard(CostLedger ledger, ConfigStore config, ICloudSessions cloud, IOwnerMessages owner, ControllerManager controller)
{
    public const decimal ExtendThreshold = 0.9m;
    public static readonly IReadOnlyList<decimal> ExtendAmounts = [25m, 100m];

    /// <summary>Computes the views only (no interrupts, no messages, nothing saved).</summary>
    public CostPass Build(IReadOnlyCollection<string> liveIds)
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
            // Raising one session's limit cannot lift a fyrd-wide block, so no raise is offered then.
            views[id] = new HubCostView(c.Cost, limit, ratio, c.Estimated, blocked,
                !exempt && !fyrdBlocked && c.Cost >= limit * ExtendThreshold, exempt);
        }
        return new CostPass(views, new HubBudgetView(
            ledger.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ledger.TimeZone.Id, total, cfg.FyrdDailyLimitUsd, fyrdBlocked));
    }

    public enum ExtendStatus { Ok, NoData, Exempt, TooEarly, FyrdBlocked }

    /// <summary>
    /// Raises a session's limit for today. The check runs against freshly built views and the raise happens under the
    /// same ledger lock, so two quick requests cannot both pass the 90% check.
    /// </summary>
    public (ExtendStatus Status, HubCostView? View) TryExtend(string id, decimal amountUsd, IReadOnlyCollection<string> liveIds) =>
        ledger.Atomically(() =>
        {
            var (views, budget) = Build(liveIds);
            if (!views.TryGetValue(id, out var v)) return (ExtendStatus.NoData, (HubCostView?)null);
            if (v.Exempt) return (ExtendStatus.Exempt, v);
            if (budget.FyrdBlocked) return (ExtendStatus.FyrdBlocked, v);
            if (!v.CanExtend) return (ExtendStatus.TooEarly, v);
            ledger.AddExtra(id, amountUsd);
            return (ExtendStatus.Ok, Build(liveIds).Views[id]);
        });

    /// <summary>
    /// Records today's figures, then interrupts and notifies for anything newly over a limit. Runs after the poll's
    /// views are published, so slow HTTP calls never delay them. The endpoints' 409 checks use the published views, so
    /// a session can still be messaged for up to one poll after it crosses its limit.
    /// </summary>
    /// <param name="live">Every session the poll lists, with its title.</param>
    /// <param name="controllerLive">Whether the Controller was read as live this pass, so a message can reach it.</param>
    /// <returns>The first failure to report (retried next pass), or null.</returns>
    public async Task<string?> EnforceAsync(
        IReadOnlyList<(string Id, string Title)> live, CostPass pass, bool controllerLive, CancellationToken ct)
    {
        var views = pass.Views;
        var budget = pass.Budget;
        foreach (var (id, title) in live)
            if (views.TryGetValue(id, out var v)) ledger.Record(id, title, v.CostToday);

        var cfg = config.Load();
        var controllerId = controller.CurrentId;
        string? error = null;

        foreach (var (id, title) in live)
        {
            if (!views.TryGetValue(id, out var v) || v.Exempt || v.Blocked != "session") continue;
            try
            {
                // Interrupt and notify are separate steps with their own markers, so a Controller that is briefly
                // unreadable gets told later without interrupting the session again.
                if (ledger.InterruptedAtLimit(id) != v.LimitUsd)
                {
                    await cloud.InterruptAsync(id, ct);
                    ledger.SetInterrupted(id, v.LimitUsd);
                }
                if (ledger.NotifiedAtLimit(id) == v.LimitUsd) continue;
                if (controllerId is null) { ledger.SetNotified(id, v.LimitUsd); continue; } // nobody to tell
                if (!controllerLive) continue; // a Controller exists but could not be read: tell it next pass
                await owner.SendAsync(controllerId,
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
                await owner.SendAsync(controllerId,
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
        if (ledger.SaveError is { } save) error ??= $"budget: could not save costs.json: {save}";
        return error;
    }

    private static string Usd(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Auth failures and cancellation end the pass; anything else is reported and retried on the next pass.</summary>
    private static bool IsRetryable(Exception e, CancellationToken ct) =>
        e is not (CloudSessionAuthException or ClaudeAuthMissingException)
        && (e is not OperationCanceledException || !ct.IsCancellationRequested);
}
