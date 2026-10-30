namespace Fyrd.Core.Specs;

public enum SpecStatus { Draft, Queued, Researching, Building, FinalReview, ReadyForHandoff, PrOpened, Failed }

public enum UnitStage { Implement, Review, Validate, Done }

public enum UnitState { Running, Idle, WaitingOnOwner, Blocked, Done }

public sealed record SpecRun(
    string Id,
    string Title,
    string Repo,
    SpecStatus Status,
    DateTimeOffset CreatedAt,
    string? SessionId = null,
    long CostCents = 0,
    string? LastEventId = null,
    string? Branch = null,
    string? Error = null);

public sealed record Unit(string Id, string SpecId, string Title, UnitStage Stage, UnitState State);

/// <summary>Run is null and Error is set when the spec's run.json is unreadable.</summary>
public sealed record SpecListEntry(SpecRun? Run, string Id, string? Error);
