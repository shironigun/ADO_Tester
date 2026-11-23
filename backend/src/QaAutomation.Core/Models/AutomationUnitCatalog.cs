namespace QaAutomation.Core.Models;

/// <summary>
/// Known automation-unit slugs + aliases (Plan suite mapping) and New Scan selectable entries.
/// Nested product surfaces (CPQ, Data Review, Feedback) are first-class units — not collapsed
/// into their side-menu parent.
/// </summary>
public static class AutomationUnitCatalog
{
    /// <summary>slug → display/alias tokens (case-insensitive contains / equals).</summary>
    public static IReadOnlyList<(string Slug, string[] Aliases)> Units { get; } =
    [
        ("cpq-plus", ["cpq+", "cpq plus", "cpq-plus", "cpq"]),
        ("customers-data-review", ["data review", "customers-data-review", "data-review"]),
        ("survey-feedback", ["survey feedback", "survey-feedback", "feedback"]),
        ("deals", ["deals", "deal"]),
        ("customers", ["customers", "customer"]),
        ("messaging", ["messaging", "messages", "inbox"]),
        ("inventory", ["inventory"]),
        ("orders", ["orders", "order"]),
        ("payment", ["payment", "payments"]),
        ("subscriptions", ["subscriptions", "subscription"]),
        ("tasks", ["tasks", "task"]),
        ("timeline", ["timeline"]),
        ("broadcast", ["broadcast"]),
        ("automation", ["automation"]),
        ("dashboard", ["dashboard"]),
        ("settings", ["settings"]),
        ("social-media", ["social media", "social-media"]),
        ("survey", ["survey"]),
        ("videocall", ["videocall", "video call", "video-call"]),
        ("widget", ["widget"]),
        ("user-profile", ["user profile", "user-profile", "profile"]),
        ("dealer", ["dealer", "dealers"]),
        ("authentication", ["authentication", "auth", "login"]),
    ];

    public static IReadOnlyList<string> Slugs => Units.Select(u => u.Slug).ToList();

    /// <summary>
    /// Units shown in New Scan. Nested surfaces carry parent context for the dropdown label.
    /// Paths are FRS atlas defaults (repo-relative). Data Review / Feedback deferred until FRS docs exist.
    /// </summary>
    public static IReadOnlyList<AutomationUnitChoice> Selectable { get; } =
    [
        new("deals", "Deals", null, "/deals",
            "src/components/deals",
            ["src/components/deals", "src/services/lead"]),
        new("cpq-plus", "CPQ+", "Deals", "/cpq",
            "src/components/cpq-plus",
            [
                "src/components/cpq-plus",
                "cpq-plus",
            ]),
        new("customers", "Customers", null, "/customers",
            "src/components/customer",
            ["src/components/customer"]),
        new("survey", "Survey", null, "/survey",
            "src/components/survey",
            ["src/components/survey", "messaging"]),
        new("messaging", "Messaging", null, "/messaging",
            "src/components/messaging",
            ["src/components/messaging", "messaging"]),
        new("inventory", "Inventory", null, "/inventory",
            "src/components/inventory",
            ["src/components/inventory"]),
        new("orders", "Orders", null, "/orders",
            "src/components/orders",
            ["src/components/orders"]),
        new("payment", "Payment", null, "/payment",
            "src/components/payment",
            ["src/components/payment"]),
        new("dashboard", "Dashboard", null, "/dashboard",
            "src/components/dashboard",
            ["src/components/dashboard"]),
        new("tasks", "Tasks", null, "/tasks",
            "src/components/deals",
            ["src/components/deals", "src/services/lead"]),
        new("settings", "Settings", null, "/settings",
            "src/components/setting",
            ["src/components/setting"]),
        new("broadcast", "Broadcast", null, "/broadcast",
            "src/components/broadcast",
            ["src/components/broadcast"]),
        new("timeline", "Timeline", null, "/timeline",
            "src/components/timeline",
            ["src/components/timeline"]),
        new("dealer", "Dealer", null, "/dealer",
            "src/components/dealer",
            ["src/components/dealer", "dealer"]),
        new("authentication", "Authentication", null, "/login",
            "src/components/authentication",
            ["src/components/authentication"]),
        new("user-profile", "User profile", null, "/user-profile",
            "src/components/user-profile",
            ["src/components/user-profile"]),
        new("social-media", "Social media", null, "/social-media",
            "src/components/SocialMedia",
            ["src/components/SocialMedia"]),
        new("videocall", "Video call", null, "/videocall",
            "src/components/videocall",
            ["src/components/videocall"]),
        new("widget", "Widget", null, "/widget",
            "src/components/layouts",
            ["src/components/layouts"]),
        new("automation", "Automation", null, "/automation",
            "src/components/automation",
            ["src/components/automation"]),
    ];
}

/// <param name="Slug">Disk / session / FRS / impact unit key (e.g. <c>cpq-plus</c>).</param>
/// <param name="DisplayName">Short label for the picker.</param>
/// <param name="ParentShell">Side-menu parent when nested (e.g. Deals for CPQ+); null for top-level.</param>
/// <param name="RoutePath">Primary SPA route.</param>
/// <param name="FrontendPath">Repo-relative FE folder for API tracing.</param>
/// <param name="SourceDirs">Default code-scan roots (repo-relative).</param>
public sealed record AutomationUnitChoice(
    string Slug,
    string DisplayName,
    string? ParentShell,
    string RoutePath,
    string FrontendPath,
    IReadOnlyList<string> SourceDirs)
{
    public string Label => ParentShell is null
        ? DisplayName
        : $"{DisplayName} · under {ParentShell}";
}
