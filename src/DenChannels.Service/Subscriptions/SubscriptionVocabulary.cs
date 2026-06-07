using System.Collections.ObjectModel;

namespace DenChannels.Service.Subscriptions;

/// <summary>
/// Controlled vocabulary constants for subscriptions — purposes, statuses, stream kinds.
/// ADR/cutover-plan reserved values made explicit for module validation.
/// </summary>
public static class SubscriptionVocabulary
{
    // ── Subscription purposes ──────────────────────────────────────────

    public const string OrdinaryChannel = "ordinary_channel";
    public const string AgentCommons = "agent_commons";
    public const string WorkerPoolControl = "worker_pool_control";
    public const string TargetWork = "target_work";
    public const string WorkflowAnalysis = "workflow_analysis";
    public const string CoordinationCall = "coordination_call";
    public const string Observer = "observer";

    public static readonly ReadOnlyCollection<string> AllowedPurposes = new(
    [
        OrdinaryChannel,
        AgentCommons,
        WorkerPoolControl,
        TargetWork,
        WorkflowAnalysis,
        CoordinationCall,
        Observer
    ]);

    public static readonly ReadOnlyCollection<string> NonRetiredPurposes = new(
    [
        OrdinaryChannel,
        AgentCommons,
        WorkerPoolControl,
        TargetWork,
        WorkflowAnalysis,
        CoordinationCall,
        Observer
    ]);

    public static bool IsAllowedPurpose(string? purpose) =>
        purpose is not null && AllowedPurposes.Contains(purpose);

    // ── Subscription statuses ──────────────────────────────────────────

    public const string StatusActive = "active";
    public const string StatusIdle = "idle";
    public const string StatusBusy = "busy";
    public const string StatusDegraded = "degraded";
    public const string StatusOffline = "offline";
    public const string StatusLeft = "left";
    public const string StatusReleased = "released";
    public const string StatusQuarantined = "quarantined";
    public const string StatusNeedsRebind = "needs_rebind";

    public static readonly ReadOnlyCollection<string> AllowedStatuses = new(
    [
        StatusActive, StatusIdle, StatusBusy, StatusDegraded,
        StatusOffline, StatusLeft, StatusReleased, StatusQuarantined, StatusNeedsRebind
    ]);

    public static bool IsAllowedStatus(string? status) =>
        status is not null && AllowedStatuses.Contains(status);

    // ── Cursor stream kinds ────────────────────────────────────────────

    public const string StreamKindMessages = "subscription_messages";
    public const string StreamKindCheckpoints = "subscription_checkpoints";
    public const string StreamKindActivity = "subscription_activity";

    public static readonly ReadOnlyCollection<string> AllowedStreamKinds = new(
    [
        StreamKindMessages, StreamKindCheckpoints, StreamKindActivity
    ]);

    public static bool IsAllowedStreamKind(string? kind) =>
        kind is not null && AllowedStreamKinds.Contains(kind);
}
