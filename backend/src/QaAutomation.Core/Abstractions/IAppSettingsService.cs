namespace QaAutomation.Core.Abstractions;

/// <summary>
/// UI-editable settings layer (spec B9): persists dotted-key values over env/appsettings defaults,
/// masks secrets, and applies option-backed changes to the live ADO/LLM clients without a restart.
/// </summary>
public interface IAppSettingsService
{
    /// <summary>Effective value of a dotted key (DB-persisted, else env/appsettings or static default).</summary>
    Task<string> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Flat dotted-key dict for the UI — secrets masked with a <c>{key}.set</c> companion bool.</summary>
    Task<IReadOnlyDictionary<string, object?>> GetAllForUiAsync(CancellationToken cancellationToken = default);

    /// <summary>Upsert the submitted keys (empty secret = unchanged; unknown keys ignored), then apply.</summary>
    Task<IReadOnlyDictionary<string, object?>> UpdateAsync(
        IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default);

    /// <summary>Re-sync the option overlay from the DB and rebuild the ADO/LLM clients. Run at startup + after updates.</summary>
    Task ApplyAsync(CancellationToken cancellationToken = default);
}
