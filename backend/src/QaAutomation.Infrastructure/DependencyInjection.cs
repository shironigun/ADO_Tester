using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Infrastructure.AzureDevOps;
using QaAutomation.Infrastructure.Batches;
using QaAutomation.Infrastructure.Data;
using QaAutomation.Infrastructure.Events;
using QaAutomation.Infrastructure.Generation;
using QaAutomation.Infrastructure.Knowledgebase;
using QaAutomation.Infrastructure.Modules;
using QaAutomation.Infrastructure.Results;
using QaAutomation.Infrastructure.Llm;
using QaAutomation.Infrastructure.Logging;
using QaAutomation.Infrastructure.Notifications;
using QaAutomation.Infrastructure.Orchestrators;
using QaAutomation.Infrastructure.QaPipelines;
using QaAutomation.Infrastructure.QaRuns;
using QaAutomation.Infrastructure.Reports;
using QaAutomation.Infrastructure.Settings;

namespace QaAutomation.Infrastructure;

/// <summary>
/// Infrastructure DI composition root. B1 added the EF data layer; B2 adds the Azure DevOps client.
/// Later phases add the LLM providers, agents, orchestrators, knowledgebase, and reports here.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration config, string sqliteConnectionString)
    {
        services.AddDbContext<QaDbContext>(o => o
            .UseSqlite(sqliteConnectionString)
            .AddInterceptors(SqliteBusyTimeoutInterceptor.Instance));
        services.AddScoped<DatabaseInitializer>();
        services.AddScoped<LeadsScanSeeder>();
        services.AddDbLogging();
        services.AddAzureDevOps(config);
        services.AddLlm(config);
        services.AddKnowledgebase(config);
        services.AddPipelineInfrastructure();
        services.AddSettingsLayer();
        services.AddReports();
        services.AddQaLoop(config);
        return services;
    }

    /// <summary>B7 reports: summarizer, HTML builder, generation service, and the weekly scheduler.</summary>
    private static IServiceCollection AddReports(this IServiceCollection services)
    {
        services.AddSingleton<ReportSummarizer>();
        services.AddSingleton<IReportHtmlBuilder, ReportHtmlBuilder>();
        services.AddSingleton<IReportService, ReportService>();
        services.AddHostedService<WeeklyReportScheduler>();
        return services;
    }

    /// <summary>B9 UI-editable settings. The RuntimeSettings overlay PostConfigures are registered AFTER
    /// the env bridge (in AddAzureDevOps/AddLlm) so a persisted DB value wins over the .env value.</summary>
    private static IServiceCollection AddSettingsLayer(this IServiceCollection services)
    {
        services.AddSingleton<RuntimeSettings>();

        services.AddOptions<AzureDevOpsOptions>().PostConfigure<RuntimeSettings>((o, rt) =>
        {
            o.Organization = rt.Get("ado.organization") ?? o.Organization;
            o.Project = rt.Get("ado.project") ?? o.Project;
            o.PersonalAccessToken = rt.Get("ado.pat") ?? o.PersonalAccessToken;
            o.Repository = rt.Get("ado.repository") ?? o.Repository;
            o.DefaultBranch = rt.Get("ado.default_branch") ?? o.DefaultBranch;
        });
        services.AddOptions<AnthropicOptions>().PostConfigure<RuntimeSettings>((o, rt) =>
        {
            o.ApiKey = rt.Get("anthropic.api_key") ?? o.ApiKey;
            o.Model = rt.Get("anthropic.model") ?? o.Model;
        });
        services.AddOptions<AzureOpenAiOptions>().PostConfigure<RuntimeSettings>((o, rt) =>
        {
            o.Endpoint = rt.Get("azure_openai.endpoint") ?? o.Endpoint;
            o.ApiKey = rt.Get("azure_openai.api_key") ?? o.ApiKey;
            o.Deployment = rt.Get("azure_openai.deployment") ?? o.Deployment;
        });
        services.AddOptions<OpenAiCompatibleOptions>().PostConfigure<RuntimeSettings>((o, rt) =>
        {
            o.BaseUrl = rt.Get("openai.base_url") ?? o.BaseUrl;
            o.ApiKey = rt.Get("openai.api_key") ?? o.ApiKey;
            o.Model = rt.Get("openai.model") ?? o.Model;
        });
        services.AddOptions<ClaudeCliOptions>().PostConfigure<RuntimeSettings>((o, rt) =>
        {
            o.Path = rt.Get("claude_cli.path") ?? o.Path;
            o.Model = rt.Get("claude_cli.model") ?? o.Model;
            if (rt.Get("claude_cli.timeout_seconds") is { } ts && int.TryParse(ts, out var t))
                o.TimeoutSeconds = Math.Max(10, t);
        });
        services.AddOptions<CopilotCliOptions>().PostConfigure<RuntimeSettings>((o, rt) =>
        {
            o.Path = rt.Get("copilot_cli.path") ?? o.Path;
            o.Model = rt.Get("copilot_cli.model") ?? o.Model;
            o.Context = rt.Get("copilot_cli.context") ?? o.Context;
            if (rt.Get("copilot_cli.timeout_seconds") is { } ts && int.TryParse(ts, out var t))
                o.TimeoutSeconds = Math.Max(10, t);
        });

        services.AddSingleton<IAppSettingsService, AppSettingsService>();
        return services;
    }

    private static IServiceCollection AddPipelineInfrastructure(this IServiceCollection services)
    {
        // Shared B5 infra: live-console pub/sub, the process-wide concurrency gate, and the
        // per-session cancellation registry. All singletons (in-memory, span the whole process).
        services.AddSingleton<IEventBus, EventBus>();
        services.AddSingleton<PipelineConcurrency>();
        services.AddSingleton<CancellationRegistry>();
        services.AddSingleton<IAgentOrchestrator, AgentOrchestrator>();
        services.AddSingleton<IBugOrchestrator, BugOrchestrator>();
        services.AddScoped<IBatchService, BatchService>();   // scoped: uses the request/background DbContext
        return services;
    }

    private static IServiceCollection AddKnowledgebase(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<StorageOptions>().Bind(config.GetSection(StorageOptions.SectionName));
        // Scoped: shares the request/background DbContext scope (FTS writes + BM25 reads go through it).
        services.AddScoped<IKnowledgebaseService, KnowledgebaseService>();
        // Track A KB foundation: reads module source → LLM → per-flow KB docs + rules.md, stored+indexed
        // through the KnowledgebaseService above (so scoped to match it).
        services.AddScoped<IKbExtractionService, KbExtractionService>();
        // Track A Generator: KB flow → LLM → Playwright spec, written into /automation. Scoped (reads KB).
        services.AddScoped<IGenerationService, GenerationService>();
        // Track A triage loop: JUnit run → triage failures → file house-style Bug on real bugs. Scoped.
        services.AddScoped<IResultsIngestService, ResultsIngestService>();
        // Track A module run: source + existing cases → discover flows + coverage map. Scoped (reads KB).
        services.AddScoped<IModuleAnalysisService, ModuleAnalysisService>();
        return services;
    }

    private static IServiceCollection AddLlm(this IServiceCollection services, IConfiguration config)
    {
        static string? Env(string key) => Environment.GetEnvironmentVariable(key);

        services.AddOptions<LlmOptions>().Bind(config.GetSection(LlmOptions.SectionName));

        services.AddOptions<AnthropicOptions>()
            .Bind(config.GetSection(AnthropicOptions.SectionName))
            .PostConfigure(o =>
            {
                o.ApiKey = Env("ANTHROPIC_API_KEY") ?? o.ApiKey;
                o.Model = Env("ANTHROPIC_MODEL") ?? o.Model;
                if (int.TryParse(Env("ANTHROPIC_MAX_TOKENS"), out var mt)) o.MaxTokens = mt;
            });

        services.AddOptions<AzureOpenAiOptions>()
            .Bind(config.GetSection(AzureOpenAiOptions.SectionName))
            .PostConfigure(o =>
            {
                o.Endpoint = Env("AZURE_OPENAI_ENDPOINT") ?? o.Endpoint;
                o.ApiKey = Env("AZURE_OPENAI_API_KEY") ?? o.ApiKey;
                o.Deployment = Env("AZURE_OPENAI_DEPLOYMENT") ?? o.Deployment;
                o.ApiVersion = Env("AZURE_OPENAI_API_VERSION") ?? o.ApiVersion;
            });

        services.AddOptions<OpenAiCompatibleOptions>()
            .Bind(config.GetSection(OpenAiCompatibleOptions.SectionName))
            .PostConfigure(o =>
            {
                o.BaseUrl = Env("OPENAI_BASE_URL") ?? o.BaseUrl;
                o.ApiKey = Env("OPENAI_API_KEY") ?? o.ApiKey;
                o.Model = Env("OPENAI_MODEL") ?? o.Model;
            });

        services.AddOptions<ClaudeCliOptions>()
            .Bind(config.GetSection(ClaudeCliOptions.SectionName))
            .PostConfigure(o =>
            {
                o.Path = Env("CLAUDE_CLI_PATH") ?? o.Path;
                o.Model = Env("CLAUDE_CLI_MODEL") ?? o.Model;
                if (int.TryParse(Env("CLAUDE_CLI_TIMEOUT_SECONDS"), out var t)) o.TimeoutSeconds = t;
            });

        services.AddOptions<CopilotCliOptions>()
            .Bind(config.GetSection(CopilotCliOptions.SectionName))
            .PostConfigure(o =>
            {
                o.Path = Env("COPILOT_CLI_PATH") ?? o.Path;
                o.Model = Env("COPILOT_CLI_MODEL") ?? o.Model;
                o.Context = Env("COPILOT_CLI_CONTEXT") ?? o.Context;
                if (int.TryParse(Env("COPILOT_CLI_TIMEOUT_SECONDS"), out var t)) o.TimeoutSeconds = t;
            });

        // Anthropic provider is built per-call on a pooled HttpClient. 10-min timeout matches the SDK default.
        services.AddHttpClient(LlmClientFactory.AnthropicHttpClientName, c => c.Timeout = TimeSpan.FromMinutes(10));

        // Provider state + factory are singletons; the factory builds a fresh client per call (spec §5.6).
        services.AddSingleton<ILlmProviderState, LlmProviderState>();
        services.AddSingleton<ILlmClientFactory, LlmClientFactory>();
        return services;
    }

    private static IServiceCollection AddAzureDevOps(this IServiceCollection services, IConfiguration config)
    {
        static string? Env(string key) => Environment.GetEnvironmentVariable(key);

        services.AddOptions<AzureDevOpsOptions>()
            .Bind(config.GetSection(AzureDevOpsOptions.SectionName))
            .PostConfigure(o =>
            {
                // Bridge the live .env (Python-style flat names) until the B9 settings layer lands.
                o.PersonalAccessToken = Env("ADOACCESS_PAT") ?? o.PersonalAccessToken;
                o.Organization = Env("ADO_ORGANIZATION") ?? o.Organization;
                o.Project = Env("ADO_PROJECT") ?? o.Project;
                o.ApiVersion = Env("ADO_API_VERSION") ?? o.ApiVersion;
                o.Repository = Env("ADO_REPOSITORY") ?? o.Repository;
                o.DefaultBranch = Env("ADO_DEFAULT_BRANCH") ?? o.DefaultBranch;
            });

        services.AddTransient<AdoAuthHandler>();
        services.AddHttpClient(AzureDevOpsClient.HttpClientName, c =>
            {
                c.Timeout = TimeSpan.FromSeconds(30);   // override the 100s default (spec §5.5)
                c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            })
            .AddHttpMessageHandler<AdoAuthHandler>();

        // Singleton so the 60s work-item cache + repo-id cache persist (matches the Python process singleton).
        services.AddSingleton<IAzureDevOpsClient>(sp =>
            new AzureDevOpsClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(AzureDevOpsClient.HttpClientName),
                sp.GetRequiredService<IOptionsMonitor<AzureDevOpsOptions>>(),
                sp.GetRequiredService<ILogger<AzureDevOpsClient>>()));

        return services;
    }

    /// <summary>QA Loop Track B: orchestrator, runner, run-execution, watcher, notifier, agents, options.</summary>
    private static IServiceCollection AddQaLoop(this IServiceCollection services, IConfiguration config)
    {
        static string? Env(string key) => Environment.GetEnvironmentVariable(key);

        // .env bridge → QaLoopOptions, then RuntimeSettings overlay (DB-over-env).
        services.AddOptions<QaLoopOptions>()
            .Bind(config.GetSection(QaLoopOptions.SectionName))
            .PostConfigure(o =>
            {
                o.TeamsWebhookUrl = Env("TEAMS_WEBHOOK_URL") ?? o.TeamsWebhookUrl;
                o.TeamsTemplate = Env("TEAMS_TEMPLATE") ?? o.TeamsTemplate;
                if (Env("QALOOP_POLL_INTERVAL_MINUTES") is { } pi && int.TryParse(pi, out var piv))
                    o.PollIntervalMinutes = Math.Max(1, piv);
                o.ReportProvider = Env("QALOOP_REPORT_PROVIDER") ?? o.ReportProvider;
                if (Env("QALOOP_ACTIVITY_ENABLED") is { } ae && bool.TryParse(ae, out var aev))
                    o.ActivityEnabled = aev;
                o.FrontendRepo = Env("QALOOP_FRONTEND_REPO") ?? o.FrontendRepo;
                o.FrontendRoot = Env("QALOOP_FRONTEND_ROOT") ?? o.FrontendRoot;
                o.FrontendBaseUrl = Env("QALOOP_FRONTEND_BASE_URL") ?? o.FrontendBaseUrl;
            })
            .PostConfigure<RuntimeSettings>((o, rt) =>
            {
                o.TeamsWebhookUrl = rt.Get("teams.webhook_url") ?? o.TeamsWebhookUrl;
                o.TeamsTemplate = rt.Get("teams.template") ?? o.TeamsTemplate;
                if (rt.Get("qaloop.poll_interval_minutes") is { } pi && int.TryParse(pi, out var piv))
                    o.PollIntervalMinutes = Math.Max(1, piv);
                o.ReportProvider = rt.Get("qaloop.report_provider") ?? o.ReportProvider;
                if (rt.Get("qaloop.activity_enabled") is { } ae && bool.TryParse(ae, out var aev))
                    o.ActivityEnabled = aev;
                o.FrontendRepo = rt.Get("qaloop.frontend_repo") ?? o.FrontendRepo;
                o.FrontendRoot = rt.Get("qaloop.frontend_root") ?? o.FrontendRoot;
                o.FrontendBaseUrl = rt.Get("qaloop.frontend_base_url") ?? o.FrontendBaseUrl;
            });

        // Automation target + pre-run auth (single base URL for all DMS; bearer minted per run).
        services.AddOptions<AutomationAuthOptions>()
            .Bind(config.GetSection(AutomationAuthOptions.SectionName))
            .PostConfigure(o =>
            {
                o.LeadApiBaseUrl = Env("LEAD_API_BASE_URL") ?? o.LeadApiBaseUrl;
                o.WebApiBaseUrl  = Env("WEB_API_BASE_URL") ?? o.WebApiBaseUrl;
                o.AuthDealerId   = Env("AUTH_DEALER_ID") ?? o.AuthDealerId;
                o.AuthUsername   = Env("AUTH_USERNAME") ?? o.AuthUsername;
                o.AuthPassword   = Env("AUTH_PASSWORD") ?? o.AuthPassword;
            });
        services.AddSingleton<IAutomationAuthenticator, AutomationAuthenticator>();

        services.AddSingleton<IQaLoopOrchestrator, QaLoopOrchestrator>();
        services.AddSingleton<AllureEnricher>();
        services.AddSingleton<IPlaywrightRunner, PlaywrightRunner>();
        services.AddScoped<RunExecutionService>();
        services.AddSingleton<ITeamsNotifier, TeamsNotifier>();
        // Registered as its own concrete type (not just AddHostedService<T>, which only exposes T as
        // IHostedService) so QaPipelinesController can inject the SAME singleton instance directly —
        // AddHostedService<T> alone leaves T unresolvable by its own type and crashes every controller action.
        services.AddSingleton<PipelineWatcherHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<PipelineWatcherHostedService>());

        services.AddHttpClient();   // IHttpClientFactory for TeamsNotifier (idempotent if already added)

        return services;
    }

    /// <summary>Runs migrate/adopt → FTS bootstrap → seed. Call once at startup before <c>app.Run()</c>.</summary>
    public static void InitializeDatabase(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().Initialize();
        scope.ServiceProvider.GetRequiredService<LeadsScanSeeder>().Seed();
    }
}
