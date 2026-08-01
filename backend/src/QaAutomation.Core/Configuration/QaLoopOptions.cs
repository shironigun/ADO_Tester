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

    // ── UI phase Playwright framework root (Slab 2, QLV2-204) ──────────────
    /// <summary>Absolute path to the Playwright framework root (where package.json, .mcp.json, and
    /// .claude/agents/ live). Claude CLI spawns with this as cwd so it loads the MCP config and
    /// per-project agent definitions. Set via AUTOMATION_PLAYWRIGHT_ROOT.</summary>
    public string PlaywrightRoot { get; set; } = "";

    /// <summary>Per-case Playwright UI script generation timeout in seconds (KBG-112). Default 600 (10 min).</summary>
    public int UiScriptGenerationTimeoutSeconds { get; set; } = 600;

    // ── UI phase app auth (Slab 2, QLV2-202) ────────────────────────────────
    /// <summary>Base URL of the app under test for Playwright UI phase (e.g. "https://ideal.targetcrm.local").
    /// Set via AUTOMATION_APP_URL / qaloop.app_url.</summary>
    public string AppUrl { get; set; } = "";
    /// <summary>Browser-login username for Playwright global-setup storageState.
    /// Set via AUTOMATION_APP_USERNAME / qaloop.app_username.</summary>
    public string AppUsername { get; set; } = "";
    /// <summary>Browser-login password for Playwright global-setup storageState (treated as secret).
    /// Set via AUTOMATION_APP_PASSWORD / qaloop.app_password.</summary>
    public string AppPassword { get; set; } = "";

    // ── Derived repo-relative source paths for the QA-Loop scanners ──────────────
    // All derive from FrontendRoot (the SPA src root) so the product-repo layout is configured in one
    // place (QALOOP_FRONTEND_ROOT). Empty FrontendRoot → bare relative fallbacks (unconfigured no-op).
    /// <summary>Module component dir, e.g. FrontendRoot + "/components/{module}".</summary>
    public string FrontendComponentsDir(string module) => Join(FrontendRoot, $"components/{module}");

    /// <summary>Module e2e-tests dir, a sibling of the SPA src root: repo-root + "/tests-e2e/{module}".</summary>
    public string FrontendTestsDir(string module) => Join(FrontendRepoRoot, $"tests-e2e/{module}");

    /// <summary>Greenfield automation folder, a sibling of the SPA src root: repo-root + "/automation".</summary>
    public string AutomationRoot => Join(FrontendRepoRoot, "automation");

    /// <summary>FrontendRoot's parent (the product-repo root), e.g. ".../app/src" → ".../app". Empty when unset.</summary>
    private string FrontendRepoRoot => FrontendRoot.Contains('/') ? FrontendRoot[..FrontendRoot.LastIndexOf('/')] : "";

    private static string Join(string root, string rel) => string.IsNullOrEmpty(root) ? rel : $"{root}/{rel}";
}
