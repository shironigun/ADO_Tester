using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Data;

namespace QaAutomation.Infrastructure.Settings;

/// <summary>
/// UI-editable settings layer (spec B9). Persists dotted-key values in the <c>Settings</c> table over
/// env/appsettings defaults, masks secrets for display, and on <see cref="ApplyAsync"/> pushes the
/// option-backed values into <see cref="RuntimeSettings"/> and evicts the options caches so the ADO/LLM
/// clients rebuild with the new values (DB-over-env). Singleton.
/// </summary>
public sealed class AppSettingsService : IAppSettingsService
{
    // Every dotted key, in the exact GET/PUT /config iteration order (frozen contract).
    public static readonly string[] AllKeys =
    [
        "ado.organization", "ado.project", "ado.pat", "ado.repository", "ado.default_branch",
        "anthropic.api_key", "anthropic.model",
        "azure_openai.endpoint", "azure_openai.api_key", "azure_openai.deployment",
        "openai.base_url", "openai.api_key", "openai.model",
        "claude_cli.path", "claude_cli.model", "claude_cli.timeout_seconds",
        "copilot_cli.path", "copilot_cli.model", "copilot_cli.context", "copilot_cli.timeout_seconds",
        "defaults.test_plan_id", "defaults.test_suite_id",
        "reports.default_boards", "reports.schedule_enabled", "reports.default_range_days",
        "reports.team_name", "reports.ticket_types", "reports.date_filter_fields",
        "find_bugs.verify_detail", "find_bugs.verify_step_scope", "find_bugs.bug_from_case_mode",
        "teams.webhook_url", "teams.template",
        "qaloop.poll_interval_minutes", "qaloop.report_provider", "qaloop.activity_enabled",
        "qaloop.frontend_repo", "qaloop.frontend_root", "qaloop.frontend_base_url",
        "qaloop.default_repository", "qaloop.default_branch",
        "qaloop.default_plan_id", "qaloop.default_suite_id",
    ];

    public static readonly IReadOnlySet<string> SecretKeys =
        new HashSet<string> { "ado.pat", "anthropic.api_key", "azure_openai.api_key", "openai.api_key", "teams.webhook_url" };

    /// <summary>The option-backed keys whose effective value lives in the typed options (DB-or-env).</summary>
    private static readonly IReadOnlySet<string> OptionKeys = new HashSet<string>
    {
        "ado.organization", "ado.project", "ado.pat", "ado.repository", "ado.default_branch",
        "anthropic.api_key", "anthropic.model",
        "azure_openai.endpoint", "azure_openai.api_key", "azure_openai.deployment",
        "openai.base_url", "openai.api_key", "openai.model",
        "claude_cli.path", "claude_cli.model", "claude_cli.timeout_seconds",
        "copilot_cli.path", "copilot_cli.model", "copilot_cli.context", "copilot_cli.timeout_seconds",
        "teams.webhook_url", "teams.template",
        "qaloop.poll_interval_minutes", "qaloop.report_provider", "qaloop.activity_enabled",
        "qaloop.frontend_repo", "qaloop.frontend_root", "qaloop.frontend_base_url",
    };

    // Static defaults for the non-option keys when unset (frozen env-default map).
    private static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>
    {
        ["defaults.test_plan_id"] = "", ["defaults.test_suite_id"] = "",
        ["reports.default_boards"] = "", ["reports.schedule_enabled"] = "true",
        ["reports.default_range_days"] = "7", ["reports.team_name"] = "",
        ["reports.ticket_types"] = "User Story;Bug;Defect;Task",
        ["reports.date_filter_fields"] = "Changed",
        ["find_bugs.verify_detail"] = "step", ["find_bugs.verify_step_scope"] = "fails_only",
        ["find_bugs.bug_from_case_mode"] = "instant",
        ["qaloop.default_repository"] = "", ["qaloop.default_branch"] = "",
        ["qaloop.default_plan_id"] = "", ["qaloop.default_suite_id"] = "",
    };

    private const string Mask = "••••••••";   // 8 bullets

    private readonly IServiceScopeFactory _scopes;
    private readonly RuntimeSettings _runtime;
    private readonly IOptionsMonitor<AzureDevOpsOptions> _ado;
    private readonly IOptionsMonitor<AnthropicOptions> _anthropic;
    private readonly IOptionsMonitor<AzureOpenAiOptions> _azure;
    private readonly IOptionsMonitor<OpenAiCompatibleOptions> _openai;
    private readonly IOptionsMonitor<ClaudeCliOptions> _claudeCli;
    private readonly IOptionsMonitor<CopilotCliOptions> _copilotCli;
    private readonly IOptionsMonitor<QaLoopOptions> _qaLoop;
    private readonly IOptionsMonitorCache<AzureDevOpsOptions> _adoCache;
    private readonly IOptionsMonitorCache<AnthropicOptions> _anthropicCache;
    private readonly IOptionsMonitorCache<AzureOpenAiOptions> _azureCache;
    private readonly IOptionsMonitorCache<OpenAiCompatibleOptions> _openaiCache;
    private readonly IOptionsMonitorCache<ClaudeCliOptions> _claudeCliCache;
    private readonly IOptionsMonitorCache<CopilotCliOptions> _copilotCliCache;
    private readonly IOptionsMonitorCache<QaLoopOptions> _qaLoopCache;
    private readonly ILogger<AppSettingsService> _log;

    public AppSettingsService(
        IServiceScopeFactory scopes, RuntimeSettings runtime,
        IOptionsMonitor<AzureDevOpsOptions> ado, IOptionsMonitor<AnthropicOptions> anthropic,
        IOptionsMonitor<AzureOpenAiOptions> azure, IOptionsMonitor<OpenAiCompatibleOptions> openai,
        IOptionsMonitor<ClaudeCliOptions> claudeCli, IOptionsMonitor<CopilotCliOptions> copilotCli,
        IOptionsMonitor<QaLoopOptions> qaLoop,
        IOptionsMonitorCache<AzureDevOpsOptions> adoCache, IOptionsMonitorCache<AnthropicOptions> anthropicCache,
        IOptionsMonitorCache<AzureOpenAiOptions> azureCache, IOptionsMonitorCache<OpenAiCompatibleOptions> openaiCache,
        IOptionsMonitorCache<ClaudeCliOptions> claudeCliCache, IOptionsMonitorCache<CopilotCliOptions> copilotCliCache,
        IOptionsMonitorCache<QaLoopOptions> qaLoopCache,
        ILogger<AppSettingsService> log)
    {
        _scopes = scopes;
        _runtime = runtime;
        _ado = ado;
        _anthropic = anthropic;
        _azure = azure;
        _openai = openai;
        _claudeCli = claudeCli;
        _copilotCli = copilotCli;
        _qaLoop = qaLoop;
        _adoCache = adoCache;
        _anthropicCache = anthropicCache;
        _azureCache = azureCache;
        _openaiCache = openaiCache;
        _claudeCliCache = claudeCliCache;
        _copilotCliCache = copilotCliCache;
        _qaLoopCache = qaLoopCache;
        _log = log;
    }

    /// <summary>Effective value of a key: DB-persisted, else env/appsettings (option keys) or static default.</summary>
    public async Task<string> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var db = await LoadAsync(cancellationToken);
        return Effective(key, db);
    }

    /// <summary>Flat dotted-key dict for the UI (secrets masked + a <c>{key}.set</c> companion bool).</summary>
    public async Task<IReadOnlyDictionary<string, object?>> GetAllForUiAsync(CancellationToken cancellationToken = default)
    {
        var db = await LoadAsync(cancellationToken);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in AllKeys)
        {
            var value = Effective(key, db);
            if (SecretKeys.Contains(key))
            {
                result[key] = string.IsNullOrEmpty(value) ? "" : Mask;
                result[$"{key}.set"] = !string.IsNullOrEmpty(value);
            }
            else
            {
                result[key] = value;
            }
        }
        return result;
    }

    /// <summary>Upsert submitted keys (empty secret = unchanged; unknown keys ignored) then apply.</summary>
    public async Task<IReadOnlyDictionary<string, object?>> UpdateAsync(
        IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default)
    {
        using (var scope = _scopes.CreateScope())
        {
            var dbCtx = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            foreach (var (key, raw) in values)
            {
                if (!OptionKeys.Contains(key) && !Defaults.ContainsKey(key))
                    continue;   // silently ignore unknown keys
                var value = raw ?? "";
                if (SecretKeys.Contains(key) && value.Length == 0)
                    continue;   // empty secret = keep existing
                var row = await dbCtx.Settings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
                if (row is null)
                    dbCtx.Settings.Add(new AppSetting { Key = key, Value = value });
                else
                    row.Value = value;
            }
            await dbCtx.SaveChangesAsync(cancellationToken);
        }

        await ApplyAsync(cancellationToken);
        return await GetAllForUiAsync(cancellationToken);
    }

    /// <summary>Re-sync the option overlay from the DB and evict the options caches so the ADO/LLM
    /// clients rebuild with the new values (no restart). Called at startup and after each update.</summary>
    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        var db = await LoadAsync(cancellationToken);
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in OptionKeys)
            if (db.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v))
                overrides[key] = v;
        _runtime.Replace(overrides);

        // Evict so the next CurrentValue rebuilds (re-runs Configure + env + RuntimeSettings PostConfigure).
        _adoCache.Clear();
        _anthropicCache.Clear();
        _azureCache.Clear();
        _openaiCache.Clear();
        _claudeCliCache.Clear();
        _copilotCliCache.Clear();
        _qaLoopCache.Clear();
    }

    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var dbCtx = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        return await dbCtx.Settings.AsNoTracking()
            .Where(s => AllKeys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value, StringComparer.Ordinal, cancellationToken);
    }

    private string Effective(string key, IReadOnlyDictionary<string, string> db)
    {
        // Option-backed keys: the typed option already reflects DB-over-env (via the overlay PostConfigure).
        if (OptionKeys.Contains(key))
            return OptionValue(key);
        return db.TryGetValue(key, out var v) && v.Length > 0 ? v : Defaults.GetValueOrDefault(key, "");
    }

    private string OptionValue(string key) => key switch
    {
        "ado.organization" => _ado.CurrentValue.Organization,
        "ado.project" => _ado.CurrentValue.Project,
        "ado.pat" => _ado.CurrentValue.PersonalAccessToken,
        "ado.repository" => _ado.CurrentValue.Repository,
        "ado.default_branch" => _ado.CurrentValue.DefaultBranch,
        "anthropic.api_key" => _anthropic.CurrentValue.ApiKey,
        "anthropic.model" => _anthropic.CurrentValue.Model,
        "azure_openai.endpoint" => _azure.CurrentValue.Endpoint,
        "azure_openai.api_key" => _azure.CurrentValue.ApiKey,
        "azure_openai.deployment" => _azure.CurrentValue.Deployment,
        "openai.base_url" => _openai.CurrentValue.BaseUrl,
        "openai.api_key" => _openai.CurrentValue.ApiKey,
        "openai.model" => _openai.CurrentValue.Model,
        "claude_cli.path" => _claudeCli.CurrentValue.Path,
        "claude_cli.model" => _claudeCli.CurrentValue.Model,
        "claude_cli.timeout_seconds" => _claudeCli.CurrentValue.TimeoutSeconds.ToString(),
        "copilot_cli.path" => _copilotCli.CurrentValue.Path,
        "copilot_cli.model" => _copilotCli.CurrentValue.Model,
        "copilot_cli.context" => _copilotCli.CurrentValue.Context,
        "copilot_cli.timeout_seconds" => _copilotCli.CurrentValue.TimeoutSeconds.ToString(),
        "teams.webhook_url" => _qaLoop.CurrentValue.TeamsWebhookUrl,
        "teams.template" => _qaLoop.CurrentValue.TeamsTemplate,
        "qaloop.poll_interval_minutes" => _qaLoop.CurrentValue.PollIntervalMinutes.ToString(),
        "qaloop.report_provider" => _qaLoop.CurrentValue.ReportProvider,
        "qaloop.activity_enabled" => _qaLoop.CurrentValue.ActivityEnabled.ToString().ToLowerInvariant(),
        "qaloop.frontend_repo" => _qaLoop.CurrentValue.FrontendRepo,
        "qaloop.frontend_root" => _qaLoop.CurrentValue.FrontendRoot,
        "qaloop.frontend_base_url" => _qaLoop.CurrentValue.FrontendBaseUrl,
        _ => "",
    };
}
