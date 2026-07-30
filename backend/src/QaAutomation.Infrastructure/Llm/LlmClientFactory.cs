using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;

namespace QaAutomation.Infrastructure.Llm;

/// <summary>
/// Builds a concrete <see cref="ILlmClient"/> for the active provider PER CALL (no cache), reading
/// settings at construction — so a runtime provider switch / settings edit applies next call (spec §5.6).
/// The token cap is the SHARED Anthropic max-tokens for all three API providers (do not diverge).
/// </summary>
public sealed class LlmClientFactory : ILlmClientFactory
{
    public const string AnthropicHttpClientName = "anthropic";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILlmProviderState _providerState;
    private readonly IOptionsMonitor<AnthropicOptions> _anthropic;
    private readonly IOptionsMonitor<AzureOpenAiOptions> _azure;
    private readonly IOptionsMonitor<OpenAiCompatibleOptions> _openai;
    private readonly IOptionsMonitor<OllamaOptions> _ollama;
    private readonly IOptionsMonitor<ClaudeCliOptions> _claudeCli;
    private readonly IOptionsMonitor<CopilotCliOptions> _copilotCli;
    private readonly IOptionsMonitor<StorageOptions> _storage;
    private readonly ILoggerFactory _loggerFactory;

    public LlmClientFactory(
        IHttpClientFactory httpFactory,
        ILlmProviderState providerState,
        IOptionsMonitor<AnthropicOptions> anthropic,
        IOptionsMonitor<AzureOpenAiOptions> azure,
        IOptionsMonitor<OpenAiCompatibleOptions> openai,
        IOptionsMonitor<OllamaOptions> ollama,
        IOptionsMonitor<ClaudeCliOptions> claudeCli,
        IOptionsMonitor<CopilotCliOptions> copilotCli,
        IOptionsMonitor<StorageOptions> storage,
        ILoggerFactory loggerFactory)
    {
        _httpFactory = httpFactory;
        _providerState = providerState;
        _anthropic = anthropic;
        _azure = azure;
        _openai = openai;
        _ollama = ollama;
        _claudeCli = claudeCli;
        _copilotCli = copilotCli;
        _storage = storage;
        _loggerFactory = loggerFactory;
    }

    public async Task<ILlmClient> CreateAsync(CancellationToken cancellationToken = default)
    {
        var provider = await _providerState.CurrentAsync(cancellationToken);
        var maxTokens = _anthropic.CurrentValue.MaxTokens; // shared cap (spec §5.6)

        return provider switch
        {
            LlmProvider.AzureOpenAI => BuildAzure(maxTokens),
            LlmProvider.OpenAICompatible => BuildOpenAi(maxTokens),
            LlmProvider.Ollama => BuildOllama(maxTokens),
            LlmProvider.ClaudeCli => new ClaudeCliLlmClient(_claudeCli.CurrentValue, _storage.CurrentValue, _loggerFactory.CreateLogger<ClaudeCliLlmClient>()),
            LlmProvider.CopilotCli => new CopilotCliLlmClient(_copilotCli.CurrentValue, _storage.CurrentValue, _loggerFactory.CreateLogger<CopilotCliLlmClient>()),
            _ => BuildAnthropic(maxTokens),
        };
    }

    private ILlmClient BuildAnthropic(int maxTokens)
    {
        var o = _anthropic.CurrentValue;
        return new AnthropicLlmClient(_httpFactory.CreateClient(AnthropicHttpClientName), o.ApiKey, o.BaseUrl, o.Model, maxTokens);
    }

    private ILlmClient BuildAzure(int maxTokens)
    {
        var o = _azure.CurrentValue;
        return new AzureOpenAiLlmClient(o.Endpoint, o.ApiKey, o.Deployment, maxTokens);
    }

    private ILlmClient BuildOpenAi(int maxTokens)
    {
        var o = _openai.CurrentValue;
        return new OpenAiCompatibleLlmClient(o.BaseUrl, o.ApiKey, o.Model, maxTokens);
    }

    private ILlmClient BuildOllama(int maxTokens)
    {
        var o = _ollama.CurrentValue;
        return new OllamaLlmClient(_httpFactory.CreateClient(OllamaHttpClientName), o.BaseUrl, o.ApiKey, o.Model, maxTokens);
    }

    public const string OllamaHttpClientName = "ollama";
}
