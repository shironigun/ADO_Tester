using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.SchemaExplorer;

namespace QaAutomation.Tests;

/// <summary>Safety-critical self-checks for the live-trace SQL builder: identifier bracket-quoting,
/// schema qualification, SSMS literal quoting, and the SELECT-only guard (user rule #1: read-only always).</summary>
public sealed class SchemaTraceServiceTests
{
    [Fact]
    public void Quotes_and_escapes_identifiers()
    {
        Assert.Equal("[Customer]", SchemaTraceService.QuoteIdent("Customer"));
        Assert.Equal("[weird]]name]", SchemaTraceService.QuoteIdent("weird]name"));   // ] doubled
    }

    [Fact]
    public void Qualifies_table_with_schema_when_present()
    {
        var withSchema = new SchemaTable { Name = "Customer", SqlSchema = "Customers" };
        var noSchema = new SchemaTable { Name = "Customer", SqlSchema = null };
        Assert.Equal("[Customers].[Customer]", SchemaTraceService.QualifiedTable(withSchema));
        Assert.Equal("[Customer]", SchemaTraceService.QualifiedTable(noSchema));
    }

    [Theory]
    [InlineData("123", "int", "123")]           // numeric → unquoted
    [InlineData("42", "bigint", "42")]
    [InlineData("O'Brien", "nvarchar", "'O''Brien'")]   // string → quoted, ' escaped
    [InlineData("abc", null, "'abc'")]                  // unknown type → quoted (safe default)
    public void Literal_quotes_only_non_numeric(string value, string? type, string expected)
        => Assert.Equal(expected, SchemaTraceService.Literal(value, type));

    [Fact]
    public void SelectOnly_guard_allows_select_and_rejects_the_rest()
    {
        SchemaTraceService.EnsureSelectOnly("SELECT TOP 50 * FROM [Customers].[Customer] WHERE [Id] = @p");   // ok
        Assert.Throws<TraceException>(() => SchemaTraceService.EnsureSelectOnly("DELETE FROM x"));
        Assert.Throws<TraceException>(() => SchemaTraceService.EnsureSelectOnly("UPDATE x SET y=1"));
        Assert.Throws<TraceException>(() => SchemaTraceService.EnsureSelectOnly("SELECT 1; DROP TABLE x"));   // multi-statement
    }
}
