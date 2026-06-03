using System.Net.Http.Headers;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Infrastructure.Agents;
using QaAutomation.Infrastructure.AzureDevOps;
using QaAutomation.Infrastructure.Data;
using QaAutomation.Infrastructure.Knowledgebase;
using QaAutomation.Infrastructure.Llm;

namespace QaAutomation.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ---- Storage: SQLite + local files ----
        services.AddOptions<StorageOptions>().Bind(configuration.GetSection(StorageOptions.SectionName));
        var storage = configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();
        var dataDir = Path.GetFullPath(storage.DataDirectory);
        Directory.CreateDirectory(dataDir);
        var dbPath = Path.Combine(dataDir, storage.DatabaseFileName);
        services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
        services.AddScoped<IKnowledgebaseService, KnowledgebaseService>();
        services.AddScoped<AnalystAgent>();
        services.AddScoped<TesterAgent>();
        services.AddScoped<IAgentOrchestrator, AgentOrchestrator>();
        // ---- Options (secrets sourced from env vars via .env) ----
        services.AddOptions<AzureDevOpsOptions>()
            .Bind(configuration.GetSection(AzureDevOpsOptions.SectionName))
            .PostConfigure(o => SetFromEnv("ADOACCESS_PAT", v => o.PersonalAccessToken = v));

        services.AddOptions<AnthropicOptions>()
            .Bind(configuration.GetSection(AnthropicOptions.SectionName))
            .PostConfigure(o => SetFromEnv("ANTHROPIC_API_KEY", v => o.ApiKey = v));

        services.AddOptions<AzureOpenAiOptions>()
            .Bind(configuration.GetSection(AzureOpenAiOptions.SectionName))
            .PostConfigure(o =>
            {
                SetFromEnv("AZURE_OPENAI_ENDPOINT",   v => o.Endpoint = v);
                SetFromEnv("AZURE_OPENAI_API_KEY",    v => o.ApiKey = v);
                SetFromEnv("AZURE_OPENAI_DEPLOYMENT", v => o.Deployment = v);
            });

        services.AddOptions<OpenAiCompatibleOptions>()
            .Bind(configuration.GetSection(OpenAiCompatibleOptions.SectionName))
            .PostConfigure(o =>
            {
                SetFromEnv("OPENAI_BASE_URL", v => o.BaseUrl = v);
                SetFromEnv("OPENAI_API_KEY",  v => o.ApiKey = v);
                SetFromEnv("OPENAI_MODEL",    v => o.Model = v);
            });

        services.AddOptions<LlmOptions>()
            .Bind(configuration.GetSection(LlmOptions.SectionName));

        // ---- Azure DevOps client (PAT / Basic auth) ----
        services.AddHttpClient<IAzureDevOpsClient, AzureDevOpsClient>(AzureDevOpsClient.HttpClientName)
            .ConfigureHttpClient((sp, http) =>
            {
                var pat = sp.GetRequiredService<IOptions<AzureDevOpsOptions>>().Value.PersonalAccessToken;
                var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}"));
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
                http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            });

        // ---- LLM provider clients: register both typed HttpClients, pick active by config ----
        services.AddHttpClient<AnthropicLlmClient>(AnthropicLlmClient.HttpClientName)
            .ConfigureHttpClient((sp, http) =>
            {
                var o = sp.GetRequiredService<IOptions<AnthropicOptions>>().Value;
                http.BaseAddress = new Uri(o.BaseUrl);
                http.DefaultRequestHeaders.Add("x-api-key", o.ApiKey);
                http.DefaultRequestHeaders.Add("anthropic-version", o.ApiVersion);
            });

        services.AddHttpClient<AzureOpenAiLlmClient>(AzureOpenAiLlmClient.HttpClientName)
            .ConfigureHttpClient((sp, http) =>
            {
                var o = sp.GetRequiredService<IOptions<AzureOpenAiOptions>>().Value;
                if (!string.IsNullOrWhiteSpace(o.Endpoint)) http.BaseAddress = new Uri(o.Endpoint);
                if (!string.IsNullOrWhiteSpace(o.ApiKey)) http.DefaultRequestHeaders.Add("api-key", o.ApiKey);
            });

        services.AddHttpClient<OpenAiCompatibleLlmClient>(OpenAiCompatibleLlmClient.HttpClientName)
            .ConfigureHttpClient((sp, http) =>
            {
                var o = sp.GetRequiredService<IOptions<OpenAiCompatibleOptions>>().Value;
                if (!string.IsNullOrWhiteSpace(o.BaseUrl)) http.BaseAddress = new Uri(o.BaseUrl);
                if (!string.IsNullOrWhiteSpace(o.ApiKey))
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiKey);
            });

        // Active provider is held in a singleton (persisted to SQLite so it survives restarts).
        services.AddSingleton<ILlmProviderState>(sp =>
            new LlmProviderState(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<IOptions<LlmOptions>>().Value.Provider));

        services.AddScoped<ILlmClient>(sp =>
        {
            var provider = sp.GetRequiredService<ILlmProviderState>().Current;
            return provider switch
            {
                LlmProvider.AzureOpenAI => sp.GetRequiredService<AzureOpenAiLlmClient>(),
                LlmProvider.OpenAICompatible => sp.GetRequiredService<OpenAiCompatibleLlmClient>(),
                _ => sp.GetRequiredService<AnthropicLlmClient>(),
            };
        });

        return services;
    }

    /// <summary>Creates the SQLite schema (EnsureCreated) + the FTS5 chunk table. Call at startup.</summary>
    public static void InitializeInfrastructure(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();
        KnowledgebaseService.EnsureFtsTable((SqliteConnection)db.Database.GetDbConnection());
    }

    private static void SetFromEnv(string name, Action<string> set)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value)) set(value);
    }
}
