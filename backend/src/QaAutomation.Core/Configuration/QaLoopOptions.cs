namespace QaAutomation.Core.Configuration;

/// <summary>Options for the QA Loop re-shape feature (pipeline watcher, Teams, runners).</summary>
public sealed class QaLoopOptions
{
    public const string SectionName = "QaLoop";

    public int PollIntervalMinutes { get; set; } = 5;

    public string TeamsWebhookUrl { get; set; } = "";

    /// <summary>Active Teams card template: scheduled (default) | compact | developer.</summary>
    public string TeamsTemplate { get; set; } = "scheduled";

    /// <summary>Subfolder under DataDirectory where generated Playwright specs are written.</summary>
    public string ScriptsFolderName { get; set; } = "scripts";

    /// <summary>Subfolder under DataDirectory where per-run output directories are created.</summary>
    public string RunsFolderName { get; set; } = "runs";

    /// <summary>HTML report provider: allure (default) | playwright | both.</summary>
    public string ReportProvider { get; set; } = "allure";

    /// <summary>When false, verbose SSE streaming + EventLogJson are suppressed for new rows.</summary>
    public bool ActivityEnabled { get; set; } = true;

    // ── Frontend-module-driven scan (QA Loop) ────────────────────────────────
    /// <summary>Repo holding the product SPA; the New Scan module dropdown parses its route file.
    /// Set via QALOOP_FRONTEND_REPO / qaloop.frontend_repo.</summary>
    public string FrontendRepo { get; set; } = "";

    /// <summary>Repo-relative SPA source root (holds routes/ and components/).
    /// Set via QALOOP_FRONTEND_ROOT / qaloop.frontend_root.</summary>
    public string FrontendRoot { get; set; } = "";

    /// <summary>Deployed base URL; each module's reference URL is FrontendBaseUrl + its route path.
    /// Set via QALOOP_FRONTEND_BASE_URL / qaloop.frontend_base_url.</summary>
    public string FrontendBaseUrl { get; set; } = "";
}
