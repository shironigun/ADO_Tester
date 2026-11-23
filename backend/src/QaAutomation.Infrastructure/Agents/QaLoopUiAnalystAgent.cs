using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Agents;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.Agents;

/// <summary>
/// QA Loop UI Analyst — module-scoped browser-UI analysis from FRS module brief + frontend files.
/// Separate from Author Tests' <c>AnalystAgent</c>; output shape is <see cref="QaLoopUiAnalystOutput"/>.
/// </summary>
internal sealed class QaLoopUiAnalystAgent(ILlmClient llm)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private const int MaxFrontendFiles = 40;
    private const int MaxFileChars = 8000;
    private const int MaxModuleMarkdownChars = 12_000;

    public async Task<QaLoopUiAnalystOutput> AnalyzeAsync(
        string module, string moduleMarkdown, CodeScanFile[] frontendFiles,
        StreamProgress? progress = null, Action<LlmEmit>? emit = null, CancellationToken ct = default)
    {
        var userPrompt = BuildAnalyzePrompt(module, moduleMarkdown, frontendFiles);
        return await CompleteAsync(userPrompt, progress, emit, "QaLoopUiAnalyst", ct);
    }

    public async Task<QaLoopUiAnalystOutput> RefineAsync(
        string module, QaLoopUiAnalystOutput prior, string feedback,
        StreamProgress? progress = null, Action<LlmEmit>? emit = null, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.Append($"Module: {module}\n\n");
        sb.Append("## Prior UI Analysis\n");
        sb.Append(JsonSerializer.Serialize(prior, JsonOpts));
        sb.Append("\n\n## Reviewer Feedback (improve, do NOT restart)\n");
        sb.Append(feedback.Trim());
        return await CompleteAsync(sb.ToString(), progress, emit, "QaLoopUiAnalyst(refine)", ct);
    }

    private async Task<QaLoopUiAnalystOutput> CompleteAsync(
        string userPrompt, StreamProgress? progress, Action<LlmEmit>? emit, string label, CancellationToken ct)
    {
        var data = await JsonUtils.CompleteJsonAsync(
            llm, AgentPrompts.QaLoopUiAnalystSystem, userPrompt,
            opener: '{', progress: progress, emit: emit,
            label: label, cancellationToken: ct);

        if (data is not JsonObject)
            throw new AgentException("QA Loop UI analyst did not return a JSON object.");

        var parsed = JsonSerializer.Deserialize<QaLoopUiAnalystOutput>(data.ToJsonString(), JsonOpts)
            ?? throw new AgentException("QA Loop UI analyst output deserialization returned null.");
        return Normalize(parsed);
    }

    private static string BuildAnalyzePrompt(string module, string moduleMarkdown, CodeScanFile[] frontendFiles)
    {
        var sb = new StringBuilder();
        sb.Append($"Module: {module}\n\n");

        sb.Append("## Product Brief (FRS module markdown)\n");
        var brief = moduleMarkdown ?? "";
        if (brief.Length > MaxModuleMarkdownChars)
            brief = brief[..MaxModuleMarkdownChars] + "\n/* ...truncated... */";
        sb.Append(string.IsNullOrWhiteSpace(brief) ? "(none — analyse from frontend files alone)\n" : brief);
        sb.Append('\n');

        sb.Append("\n## Frontend source files\n");
        var files = frontendFiles ?? [];
        if (files.Length == 0)
        {
            sb.Append("(none — analyse from product brief alone)\n");
        }
        else
        {
            foreach (var file in files.Take(MaxFrontendFiles))
            {
                var content = file.Content ?? "";
                if (content.Length > MaxFileChars)
                    content = content[..MaxFileChars] + "\n/* ...truncated... */";
                sb.Append($"\n### {file.Path}\n```\n{content}\n```\n");
            }
        }

        return sb.ToString();
    }

    private static QaLoopUiAnalystOutput Normalize(QaLoopUiAnalystOutput o) => new(
        Features: o.Features ?? [],
        Flows: o.Flows ?? [],
        Screens: o.Screens ?? [],
        OpenQuestions: o.OpenQuestions ?? [],
        MustTest: o.MustTest ?? [],
        MustNotTest: o.MustNotTest ?? [],
        AuthPermissionsSummary: o.AuthPermissionsSummary ?? "",
        TenantVariantsSummary: o.TenantVariantsSummary ?? "");
}
