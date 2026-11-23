using QaAutomation.Core.Models;

namespace QaAutomation.Core.Abstractions;

/// <summary>One generated spec file, reconciled between disk and the DB (Scripts Board, Slab 3
/// QLV2-302). <see cref="Type"/> is <c>"api"</c> or <c>"ui"</c>. <see cref="ReconcileState"/> is one of:
/// <list type="bullet">
/// <item><c>linked</c> — on disk, has a matching DB draft row, and carries a valid <c>@qa-meta</c> header.</item>
/// <item><c>no_meta</c> — on disk, has a matching DB draft row, but no/malformed <c>@qa-meta</c> header
/// (a legacy spec generated before QLV2-301).</item>
/// <item><c>orphan_disk</c> — on disk, but no DB draft row points at it (hand-added, or its row was deleted).</item>
/// <item><c>orphan_db</c> — a DB draft row's <c>SpecFilePath</c> doesn't exist on disk anymore (drifted).</item>
/// </list></summary>
public sealed record ScriptInventoryItem(
    string SpecFilePath, string Module, string Type, string Level, string? Bucket,
    int? AdoTestCaseId, int? DraftId, IReadOnlyList<string> Tags, QaMeta? Meta, string ReconcileState);

public static class ScriptReconcileStates
{
    public const string Linked = "linked";
    public const string NoMeta = "no_meta";
    public const string OrphanDisk = "orphan_disk";
    public const string OrphanDb = "orphan_db";
}

/// <summary>Scans both generated-spec roots (API specs under <c>{DataDirectory}/scripts/</c>, UI specs
/// under <c>{PlaywrightRoot}/generated/ui/</c>) and cross-references them against <c>ApiTestDraft</c> rows
/// to produce a live, on-demand inventory — no persisted registry table, per the "reconcile scan"
/// design (see <c>docs/progress/RalphLoop/qa-loop-v2/blueprint.md</c> → SLAB 3, QLV2-302).</summary>
public interface IScriptInventoryService
{
    Task<IReadOnlyList<ScriptInventoryItem>> GetInventoryAsync(CancellationToken ct = default);
}
