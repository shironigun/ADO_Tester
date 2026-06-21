using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Infrastructure.AzureDevOps;
using QaAutomation.Infrastructure.Data;
using QaAutomation.Infrastructure.Events;
using QaAutomation.Infrastructure.Knowledgebase;
using QaAutomation.Infrastructure.Llm;
using QaAutomation.Infrastructure.Orchestrators;

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
        services.AddAzureDevOps(config);
        services.AddLlm(config);
        services.AddKnowledgebase(config);
        services.AddPipelineInfrastructure();
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
        return services;
    }

    private static IServiceCollection AddKnowledgebase(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<StorageOptions>().Bind(config.GetSection(StorageOptions.SectionName));
        // Scoped: shares the request/background DbContext scope (FTS writes + BM25 reads go through it).
        services.AddScoped<IKnowledgebaseService, KnowledgebaseService>();
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

    /// <summary>Runs migrate/adopt → FTS bootstrap → seed. Call once at startup before <c>app.Run()</c>.</summary>
    public static void InitializeDatabase(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().Initialize();
    }
}
