using System.Text.Json;
using System.Text.Json.Nodes;
using QaAutomation.Core;
using QaAutomation.Core.Agents;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Agents;

namespace QaAutomation.Tests;

/// <summary>B4 frozen-contract tests (spec §5.7): JsonUtils (extract/coerce/retry+nudge), embedded
/// prompt anchors, AnalystOutput dual-case round-trip, and the user-prompt builders' frozen strings.</summary>
public sealed class AgentTests
{
    // ── JsonUtils.ExtractJson ──────────────────────────────────────────────
    [Theory]
    [InlineData("prefix {\"a\":1} suffix", '{', "{\"a\":1}")]
    [InlineData("```json\n[1,2]\n```", '[', "[1,2]")]
    [InlineData("no opener here", '{', "")]
    [InlineData("x {\"a\":\"}\"} y", '{', "{\"a\":\"}\"}")]   // string-aware: brace inside a string is ignored
    public void ExtractJson_pulls_balanced_structure(string raw, char opener, string expected)
        => Assert.Equal(expected, JsonUtils.ExtractJson(raw, opener));

    // ── JsonUtils.CoerceString / CoerceStringList ──────────────────────────
    [Fact]
    public void CoerceString_handles_all_kinds()
    {
        Assert.Equal("", JsonUtils.CoerceString(null));
        Assert.Equal("hi", JsonUtils.CoerceString(JsonValue.Create("hi")));
        Assert.Equal("true", JsonUtils.CoerceString(JsonValue.Create(true)));   // bool before number
        Assert.Equal("false", JsonUtils.CoerceString(JsonValue.Create(false)));
        Assert.Equal("5", JsonUtils.CoerceString(JsonValue.Create(5)));
        Assert.Equal("k1: v1; k2: 2", JsonUtils.CoerceString(new JsonObject { ["k1"] = "v1", ["k2"] = 2 }));
    }

    [Fact]
    public void CoerceStringList_handles_all_kinds()
    {
        Assert.Empty(JsonUtils.CoerceStringList(null));
        Assert.Empty(JsonUtils.CoerceStringList(JsonValue.Create("   ")));      // blank string → []
        Assert.Equal(["x"], JsonUtils.CoerceStringList(JsonValue.Create("x")));
        Assert.Equal(["5"], JsonUtils.CoerceStringList(JsonValue.Create(5)));   // scalar → single-item
        Assert.Equal(["a", "2"], JsonUtils.CoerceStringList(new JsonArray { "a", JsonValue.Create(2), null }));
    }

    // ── JsonUtils.CompleteJsonAsync ────────────────────────────────────────
    [Fact]
    public async Task CompleteJson_extracts_prose_wrapped_and_nudges_on_retry()
    {
        var llm = new StubLlmClient("garbage, no json here", "Sure: {\"a\": 1}");
        var node = await JsonUtils.CompleteJsonAsync(llm, "SYS", "U", '{', label: "Analyst");
        Assert.Equal(1, node["a"]!.GetValue<int>());
        Assert.Equal(2, llm.SystemPrompts.Count);
        Assert.Equal("SYS", llm.SystemPrompts[0]);                              // first attempt: unmodified
        Assert.Contains("IMPORTANT: Your previous response was not valid JSON", llm.SystemPrompts[1]);
        Assert.Contains("Start your reply with `{`", llm.SystemPrompts[1]);     // opener nudge
    }

    [Fact]
    public async Task CompleteJson_duplicate_keys_degrade_gracefully()
    {
        // Audit finding: JsonNode dup keys throw ArgumentException on access — must degrade to a
        // retry/AgentException, never an unwrapped crash.
        const string dup = """{"a":1,"a":2}""";
        var llm = new StubLlmClient(dup, dup, dup);
        await Assert.ThrowsAsync<AgentException>(
            () => JsonUtils.CompleteJsonAsync(llm, "S", "U", '{', attempts: 3, label: "Analyst"));
    }

    [Fact]
    public async Task CompleteJson_throws_after_all_attempts()
    {
        var llm = new StubLlmClient("nope", "still nope", "nada");
        var ex = await Assert.ThrowsAsync<AgentException>(
            () => JsonUtils.CompleteJsonAsync(llm, "S", "U", '[', attempts: 3, label: "Tester"));
        Assert.Contains("Tester returned invalid JSON after 3 attempts", ex.Message);
    }

    // ── Embedded prompt anchors (byte-exact from Python) ───────────────────
    [Fact]
    public void Prompts_contain_frozen_anchors()
    {
        Assert.Contains("You are the Analyst agent for a QA automation system.", AgentPrompts.AnalystSystem);
        Assert.Contains("\"openQuestions\"", AgentPrompts.AnalystSystem);
        Assert.Contains("[Module] - [Feature] - Verify that", AgentPrompts.TesterSystem);
        Assert.Contains("Step 1 is ALWAYS \"Login to YourProduct\"", AgentPrompts.TesterSystem);
        Assert.Contains("OBSERVATION:", AgentPrompts.BugWriterSystem);
        Assert.Contains("STATIC code review", AgentPrompts.CodeVerifierSystem);
        Assert.Contains("{ACTUAL_RULE}", AgentPrompts.CodeVerifierStepSystem);   // placeholder intact pre-substitution
        Assert.Contains("code-grounded", AgentPrompts.ActualRuleFull);
        Assert.Contains("empty string", AgentPrompts.ActualRuleLite);
    }

    // ── AnalystOutput round-trip ───────────────────────────────────────────
    [Fact]
    public void AnalystOutput_from_json_is_dual_case_and_coercing()
    {
        var node = JsonNode.Parse(
            """{"Module":"Deals","happyPath":"flow","acceptance_criteria":["a","b"],"businessRules":"single"}""");
        var a = AnalystOutput.FromJson(node);

        Assert.Equal("Deals", a.Module);                 // PascalCase key, case-insensitive
        Assert.Equal("flow", a.HappyPath);
        Assert.Equal(["a", "b"], a.AcceptanceCriteria);  // snake_case list key
        Assert.Equal(["single"], a.BusinessRules);       // scalar string coerced to single-item list

        using var doc = JsonDocument.Parse(a.ToJson());
        Assert.Equal("Deals", doc.RootElement.GetProperty("module").GetString());
        Assert.Equal("flow", doc.RootElement.GetProperty("happyPath").GetString());
        Assert.True(doc.RootElement.TryGetProperty("questionAnswers", out _));
    }

    // ── User-prompt builders (frozen strings) ──────────────────────────────
    [Fact]
    public void TesterUserPrompt_has_frozen_structure()
    {
        var a = new AnalystOutput
        {
            Module = "Deals", Feature = "Pipeline", Summary = "s", HappyPath = "h",
            AcceptanceCriteria = ["ac1"], OpenQuestions = ["q1"], QuestionAnswers = ["ans1"],
        };
        var p = TesterAgent.BuildUserPrompt(a, new AdoWorkItem { Id = 42, Title = "T" });
        Assert.Contains("Work item #42: T", p);
        Assert.Contains("Acceptance criteria:\n- ac1", p);
        Assert.Contains("- Q: q1\n  A: ans1", p);
        Assert.EndsWith("Generate the full test case array now.", p);
    }

    [Fact]
    public void CodeVerifierPrompt_uses_middle_dot_and_arrow()
    {
        var a = new AnalystOutput { Module = "Deals", Feature = "Pipeline" };
        var cases = new List<TestCaseInput>
        {
            new() { Title = "tc", Steps = [new TestCaseStep { Action = "act", Expected = "exp" }] },
        };
        var p = CodeVerifierAgent.BuildPrompt(new AdoWorkItem { Id = 7, Title = "Story" }, a, cases, "CODE", null);
        Assert.Contains("Module: Deals · Feature: Pipeline", p);
        Assert.Contains("- act → exp", p);
        Assert.Contains("## Actual repository code (excerpts)", p);
    }
}
