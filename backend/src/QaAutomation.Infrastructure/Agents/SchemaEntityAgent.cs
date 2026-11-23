using System.Text;
using System.Text.Json.Nodes;
using QaAutomation.Core;
using QaAutomation.Core.Agents;
using QaAutomation.Core.Abstractions;

namespace QaAutomation.Infrastructure.Agents;

/// <summary>One keyed table, described for the entity-registry LLM call.</summary>
public sealed record TableCatalogEntry(string Service, string Table, string Pk, string Columns);

/// <summary>An entity the AI proposes for the registry (the semantic layer over raw tables).</summary>
public sealed record ProposedEntity(
    string Key, string DisplayName, string RootTable,
    string? IdColumn, string? ScopeColumn, string DisplayColumns, string Confidence);

/// <summary>
/// Phase-C AI pass: given the parsed table catalog, propose the user-meaningful ENTITIES a QA/support
/// person would look up (Customer, Deal, Lead, Conversation, Account, Invoice…) with their root table,
/// id column, and tenant-scope column. One single-shot LLM call (ClaudeCli rate-limits hard).
/// </summary>
public sealed class SchemaEntityAgent
{
    private const string SystemPrompt = """
        You are a data architect mapping a CRM product's database for QA and support engineers. You are
        given a catalog of physical tables grouped by microservice (service | table | primary key |
        sample columns).

        Identify the user-meaningful ENTITIES a person would look up by id — the "nouns" of the product
        (e.g. Customer, Contact, Deal, Lead, Conversation, Message, Account, Invoice, Segment,
        Notification). These are the ROOTS a support engineer starts a trace from ("show me everything
        for customer 12345"). Do NOT list every table — skip join/link tables, lookup/code tables,
        stored-procedure result shapes, and pure config tables.

        Return ONLY a JSON object of this exact shape:
        {
          "entities": [
            {
              "key": "<kebab-case stable key, e.g. customer, deal, conversation>",
              "displayName": "<human name, e.g. Customer>",
              "rootTable": "<EXACT table name from the catalog>",
              "idColumn": "<the primary lookup column on rootTable, e.g. CrmCustomerId>",
              "scopeColumn": "<the account/tenant scoping column if the entity is tenant-scoped, e.g. TenantId or AccountId; else null>",
              "displayColumns": "<1-3 comma-separated human-label columns, e.g. FirstName, LastName>",
              "confidence": "high|medium|low"
            }
          ]
        }

        Rules:
        - Use ONLY table and column names that appear in the catalog. Never invent names.
        - Prefer the canonical root table for an entity (the one whose primary key names the entity).
        - Aim for the ~10-30 core entities, ordered most-important first. Quality over quantity.
        """;

    private readonly ILlmClient _llm;

    public SchemaEntityAgent(ILlmClient llm) => _llm = llm;

    public async Task<IReadOnlyList<ProposedEntity>> ProposeAsync(
        string productName, IReadOnlyList<TableCatalogEntry> catalog, CancellationToken ct = default)
    {
        if (catalog.Count == 0) return [];

        var sb = new StringBuilder();
        sb.Append("Product: ").Append(productName).Append("\n\n## Tables (service | table | pk | columns)\n");
        foreach (var t in catalog)
            sb.Append(t.Service).Append(" | ").Append(t.Table).Append(" | ").Append(t.Pk)
              .Append(" | ").Append(t.Columns).Append('\n');
        sb.Append("\nReturn the entities JSON now.");

        var data = await JsonUtils.CompleteJsonAsync(
            _llm, SystemPrompt, sb.ToString(), '{', label: "SchemaEntity", cancellationToken: ct);

        if (data is not JsonObject root || root["entities"] is not JsonArray arr)
            throw new AgentException("Entity registry agent did not return an entities array.");

        var result = new List<ProposedEntity>(arr.Count);
        foreach (var item in arr)
        {
            if (item is not JsonObject o) continue;
            var rootTable = JsonUtils.StrOr(o, "rootTable");
            if (rootTable.Length == 0) continue;
            result.Add(new ProposedEntity(
                JsonUtils.StrOr(o, "key"),
                JsonUtils.StrOr(o, "displayName"),
                rootTable,
                Nullify(JsonUtils.StrOr(o, "idColumn")),
                Nullify(JsonUtils.StrOr(o, "scopeColumn")),
                JsonUtils.StrOr(o, "displayColumns"),
                JsonUtils.StrOr(o, "confidence") is { Length: > 0 } c ? c : "medium"));
        }
        return result;
    }

    private static string? Nullify(string s) =>
        string.IsNullOrWhiteSpace(s) || s.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : s;
}
