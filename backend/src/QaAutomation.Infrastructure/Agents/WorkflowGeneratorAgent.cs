using System.Text;
using System.Text.Json.Nodes;
using QaAutomation.Core.Agents;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.Agents;

/// <summary>Generates a chained Playwright workflow spec + one ADO Test Case (one step per endpoint leg).</summary>
public sealed class WorkflowGeneratorAgent
{
    private readonly ILlmClient _llm;

    public WorkflowGeneratorAgent(ILlmClient llm) => _llm = llm;

    public async Task<GeneratedSpec> GenerateAsync(
        string module, WorkflowInfo workflow, InventoryDoc inventory,
        StreamProgress? progress = null, Action<LlmEmit>? emit = null, CancellationToken ct = default)
    {
        var userPrompt = BuildPrompt(module, workflow, inventory);

        // Pass 1: spec
        var specData = await JsonUtils.CompleteJsonAsync(
            _llm, AgentPrompts.WorkflowGeneratorSystem, userPrompt, '{', progress, emit,
            label: "WorkflowGenerator", cancellationToken: ct);

        var specCode = "";
        if (specData is JsonObject so)
            specCode = SpecNormalizer.Normalize(TitleTag.EnsureInSpec(JsonUtils.StrOr(so, "spec"), $"Workflow: {workflow.Name}"));

        // Pass 2: ADO Test Case
        var tcData = await JsonUtils.CompleteJsonAsync(
            _llm, AgentPrompts.WorkflowTestcaseSystem, userPrompt, '[', progress, emit,
            label: "WorkflowTestcase", cancellationToken: ct);

        var mod = TitleTag.TitleCaseModule(module);
        var tc = ParseTestCase(tcData) ?? FallbackTestCase(module, workflow);
        tc.Title = TitleTag.EnsureTitle(tc.Title, $"{mod} - Workflow - Verify that the {workflow.Name} workflow completes end-to-end");

        var sourceRoutes = workflow.Steps.Select(s => s.Endpoint).Distinct().ToList();
        return new GeneratedSpec
        {
            Level = "workflow",
            Title = tc.Title,
            SpecCode = specCode,
            SourceRoutes = sourceRoutes,
            TestCase = tc,
        };
    }

    private static string BuildPrompt(string module, WorkflowInfo workflow, InventoryDoc inventory)
    {
        var sb = new StringBuilder();
        sb.Append($"Module: {module}\n");
        sb.Append($"Workflow: {workflow.Name}\n");
        sb.Append($"Steps:\n");
        foreach (var s in workflow.Steps)
            sb.Append($"  - {s.Endpoint} (role: {s.Role})\n");
        sb.Append($"\nRoute details:\n");
        foreach (var step in workflow.Steps)
        {
            var route = inventory.Routes.FirstOrDefault(r =>
                string.Equals($"{r.Method} {r.Route}", step.Endpoint, StringComparison.OrdinalIgnoreCase));
            if (route is not null)
                sb.Append($"  {route.Method} {route.Route}: requestDto={route.RequestDto ?? "none"}, responseDto={route.ResponseDto ?? "none"}\n");
        }
        return sb.ToString();
    }

    private static GeneratedTestCase? ParseTestCase(JsonNode data)
    {
        var arr = data as JsonArray;
        if (arr is null || arr.Count == 0) return null;
        var item = arr[0] as JsonObject;
        if (item is null) return null;
        return new GeneratedTestCase
        {
            Title = JsonUtils.StrOr(item, "title"),
            Preconditions = "",
            Steps = ParseSteps(item["steps"] as JsonArray),
        };
    }

    private static GeneratedTestCase FallbackTestCase(string module, WorkflowInfo workflow) => new()
    {
        Title = $"{TitleTag.Pending} {TitleTag.TitleCaseModule(module)} - Workflow - Verify that the {workflow.Name} workflow completes end-to-end",
        Preconditions = "",
        Steps = workflow.Steps.Select(s => new TestCaseStep
        {
            Action = $"Send a request to {s.Endpoint}",
            Expected = $"Response returns 200 and the body reflects the {s.Role} outcome (returned resource contains the fields set by this call)",
        }).Append(new TestCaseStep
        {
            Action = $"Verify that the {workflow.Name} workflow completes end-to-end successfully",
            Expected = "All prior calls succeeded and the final resource state matches the expected outcome of the flow",
        }).ToList(),
    };

    private static List<TestCaseStep> ParseSteps(JsonArray? arr)
    {
        var steps = new List<TestCaseStep>();
        if (arr is null) return steps;
        foreach (var item in arr)
            if (item is JsonObject s)
                steps.Add(new TestCaseStep { Action = JsonUtils.StrOr(s, "action"), Expected = JsonUtils.StrOr(s, "expected") });
        return steps;
    }
}
