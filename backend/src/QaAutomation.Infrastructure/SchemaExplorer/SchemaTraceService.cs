using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Data;

namespace QaAutomation.Infrastructure.SchemaExplorer;

/// <summary>Thrown for a trace that can't proceed (no connection, no scan, unknown entity). Message is safe to surface.</summary>
public sealed class TraceException(string message) : Exception(message);

/// <summary>
/// The live entity trace, READ-ONLY end to end (user rule #1). Given an entity + id it runs a
/// schema-qualified SELECT on the root table, then a one-hop SELECT per linked table, and returns the
/// rows + the exact SSMS-ready SQL for each. Every statement is BUILT here from scanned schema
/// metadata — identifiers are validated against the scan and bracket-quoted, the id value is always a
/// parameter, results are TOP-capped, and a SELECT-only guard rejects anything that isn't a single SELECT.
/// </summary>
public sealed class SchemaTraceService
{
    private const int RowCap = 50;
    private const int MaxLinkedTables = 80;
    private const int CommandTimeoutSeconds = 15;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly QaDbContext _db;
    private readonly ILogger<SchemaTraceService> _log;

    public SchemaTraceService(QaDbContext db, ILogger<SchemaTraceService> log)
    {
        _db = db;
        _log = log;
    }

    // ── Result DTOs ─────────────────────────────────────────────────────────────

    public sealed record TableTrace(
        string Table, string? Schema, string? Service, string Direction, string? MatchColumn,
        string Sql, int RowCount, IReadOnlyList<string> Columns,
        IReadOnlyList<Dictionary<string, object?>> Rows, string? Error);

    public sealed record TraceResult(
        string Entity, string RootTable, string IdColumn, string IdValue, bool Found,
        TableTrace Root, IReadOnlyList<TableTrace> Linked, int LinkedTableCount, int TotalRows,
        string? Truncated);

    // ── Entry point ─────────────────────────────────────────────────────────────

    public async Task<TraceResult> TraceAsync(
        int productId, string entityKey, string idValue, string? environment, CancellationToken ct = default)
    {
        var connString = await ResolveConnectionAsync(productId, environment, ct)
            ?? throw new TraceException(string.IsNullOrWhiteSpace(environment)
                ? "No read-only database connection is configured for this product."
                : $"No read-only connection configured for environment '{environment}'.");

        var scan = await _db.SchemaScans
            .Where(s => s.ProductId == productId && s.Status == SchemaScanStatus.Succeeded)
            .OrderByDescending(s => s.CreatedAt).FirstOrDefaultAsync(ct)
            ?? throw new TraceException("No completed scan yet — run a scan first.");

        var entity = await _db.SchemaEntities.FirstOrDefaultAsync(e => e.ProductId == productId && e.Key == entityKey, ct)
            ?? throw new TraceException($"Unknown entity '{entityKey}'.");
        if (string.IsNullOrWhiteSpace(entity.RootTableName) || string.IsNullOrWhiteSpace(entity.IdColumn))
            throw new TraceException($"Entity '{entityKey}' has no root table / id column configured.");

        var tables = await _db.SchemaTables.Where(t => t.ScanId == scan.Id).ToListAsync(ct);
        var relations = await _db.SchemaRelations.Where(r => r.ScanId == scan.Id).ToListAsync(ct);
        var svcName = await _db.SchemaServices.Where(s => s.ScanId == scan.Id).ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        // RootTableName may be bare ("Customer") or service-qualified ("customer.Customer") depending on
        // what the AI returned — match on the last segment either way.
        var wanted = entity.RootTableName!;
        var bare = wanted.Contains('.') ? wanted[(wanted.LastIndexOf('.') + 1)..] : wanted;
        var root = tables.FirstOrDefault(t => t.Name.Equals(bare, StringComparison.OrdinalIgnoreCase) && !t.IsKeyless)
            ?? tables.FirstOrDefault(t => t.Name.Equals(bare, StringComparison.OrdinalIgnoreCase))
            ?? throw new TraceException($"Root table '{entity.RootTableName}' is not in the latest scan.");

        var rootCols = DeserializeColumns(root.ColumnsJson);
        if (!rootCols.Any(c => c.Name.Equals(entity.IdColumn, StringComparison.OrdinalIgnoreCase)))
            throw new TraceException($"Column '{entity.IdColumn}' is not on table '{root.Name}'.");

        await using var conn = new SqlConnection(connString);
        try { await conn.OpenAsync(ct); }
        catch (Exception e) { throw new TraceException($"Could not connect: {e.Message}"); }

        // Root row.
        var rootTrace = await QueryTableAsync(conn, root, svcName, entity.IdColumn, idValue, "root", ct);
        var found = rootTrace.RowCount > 0 && rootTrace.Error is null;
        var rootRow = rootTrace.Rows.FirstOrDefault();

        var linked = new List<TableTrace>();
        var byId = tables.ToDictionary(t => t.Id);
        var seen = new HashSet<int>();
        string? truncated = null;

        if (found)
        {
            foreach (var r in relations.Where(r => r.SourceTableId == root.Id || r.TargetTableId == root.Id))
            {
                if (linked.Count >= MaxLinkedTables) { truncated = $"Showing first {MaxLinkedTables} linked tables."; break; }

                if (r.TargetTableId == root.Id)
                {
                    // child → root: the child holds the FK column that equals the root id.
                    if (!byId.TryGetValue(r.SourceTableId, out var child) || child.Id == root.Id) continue;
                    if (!seen.Add(child.Id)) continue;
                    var col = r.SourceColumn ?? entity.IdColumn;
                    linked.Add(await QueryTableAsync(conn, child, svcName, col, idValue, "child", ct));
                }
                else
                {
                    // root → parent: the root holds the FK; look the parent up by that value.
                    if (!byId.TryGetValue(r.TargetTableId, out var parent) || parent.Id == root.Id) continue;
                    if (!seen.Add(parent.Id)) continue;
                    var fkCol = r.SourceColumn;
                    var fkVal = fkCol is not null && rootRow is not null ? rootRow.GetValueOrDefault(fkCol) : null;
                    if (fkCol is null || fkVal is null)
                    {
                        linked.Add(new TableTrace(parent.Name, parent.SqlSchema, ServiceOf(parent, svcName),
                            "parent", fkCol, "", 0, [], [], "Root row has no value for the foreign key — nothing to look up."));
                        continue;
                    }
                    var parentCols = DeserializeColumns(parent.ColumnsJson);
                    var matchCol = parentCols.FirstOrDefault(c => c.Name.Equals(fkCol, StringComparison.OrdinalIgnoreCase))?.Name
                        ?? parentCols.FirstOrDefault(c => c.IsPrimaryKey)?.Name;
                    if (matchCol is null)
                    {
                        linked.Add(new TableTrace(parent.Name, parent.SqlSchema, ServiceOf(parent, svcName),
                            "parent", fkCol, "", 0, [], [], "No matching key column on the parent table."));
                        continue;
                    }
                    linked.Add(await QueryTableAsync(conn, parent, svcName, matchCol, Convert.ToString(fkVal) ?? "", "parent", ct));
                }
            }
        }

        var totalRows = rootTrace.RowCount + linked.Sum(l => l.RowCount);
        return new TraceResult(entity.DisplayName, root.Name, entity.IdColumn, idValue, found,
            rootTrace, linked, linked.Count, totalRows, truncated);
    }

    /// <summary>Test a connection with a trivial read-only <c>SELECT 1</c>.</summary>
    public async Task TestConnectionAsync(string connectionString, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT 1", conn) { CommandTimeout = CommandTimeoutSeconds };
        await cmd.ExecuteScalarAsync(ct);
    }

    // ── Query one table, capturing rows + the exact SQL ────────────────────────────

    private async Task<TableTrace> QueryTableAsync(
        SqlConnection conn, SchemaTable table, IReadOnlyDictionary<int, string> svcName,
        string whereColumn, string value, string direction, CancellationToken ct)
    {
        var cols = DeserializeColumns(table.ColumnsJson);
        var whereCol = cols.FirstOrDefault(c => c.Name.Equals(whereColumn, StringComparison.OrdinalIgnoreCase));
        var qualified = QualifiedTable(table);
        var svc = ServiceOf(table, svcName);

        if (whereCol is null)
            return new TableTrace(table.Name, table.SqlSchema, svc, direction, whereColumn, "", 0, [], [],
                $"Column '{whereColumn}' not found on this table in the scan.");

        var sql = $"SELECT TOP {RowCap} * FROM {qualified} WHERE {QuoteIdent(whereCol.Name)} = @p";
        EnsureSelectOnly(sql);
        var displaySql = $"SELECT TOP {RowCap} * FROM {qualified} WHERE {QuoteIdent(whereCol.Name)} = {Literal(value, whereCol.DataType)}";

        try
        {
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = CommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@p", value);
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            var rows = new List<Dictionary<string, object?>>();
            while (rows.Count < RowCap && await reader.ReadAsync(ct))
            {
                var row = new Dictionary<string, object?>(columns.Count);
                for (var i = 0; i < columns.Count; i++) row[columns[i]] = Normalize(reader.GetValue(i));
                rows.Add(row);
            }
            return new TableTrace(table.Name, table.SqlSchema, svc, direction, whereCol.Name, displaySql, rows.Count, columns, rows, null);
        }
        catch (Exception e)
        {
            _log.LogDebug(e, "trace query failed for {Table}", table.Name);
            return new TableTrace(table.Name, table.SqlSchema, svc, direction, whereCol.Name, displaySql, 0, [], [], e.Message);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private async Task<string?> ResolveConnectionAsync(int productId, string? environment, CancellationToken ct)
    {
        // Product-level (ServiceName == null) connection for the chosen environment; falls back to the
        // default/most-recent environment when none is specified. Per-service overrides come later.
        var q = _db.SchemaConnections.Where(x => x.ProductId == productId && x.ServiceName == null);
        if (!string.IsNullOrWhiteSpace(environment)) q = q.Where(x => x.Environment == environment);
        var c = await q.OrderByDescending(x => x.IsDefault).ThenByDescending(x => x.UpdatedAt).FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(c?.ConnectionString) ? null : c!.ConnectionString;
    }

    private static string ServiceOf(SchemaTable t, IReadOnlyDictionary<int, string> svcName) =>
        t.ServiceId is { } id ? svcName.GetValueOrDefault(id, "") : "";

    internal static string QuoteIdent(string name) => "[" + name.Replace("]", "]]") + "]";

    internal static string QualifiedTable(SchemaTable t) =>
        string.IsNullOrWhiteSpace(t.SqlSchema) ? QuoteIdent(t.Name) : $"{QuoteIdent(t.SqlSchema!)}.{QuoteIdent(t.Name)}";

    /// <summary>Defense in depth: everything we build is a single SELECT — assert it, so a future edit that
    /// slips in anything else fails loudly rather than running against the product DB.</summary>
    internal static void EnsureSelectOnly(string sql)
    {
        if (!sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            throw new TraceException("Internal guard: refusing a non-SELECT statement.");
        if (sql.Contains(';'))
            throw new TraceException("Internal guard: refusing a multi-statement query.");
    }

    /// <summary>SSMS-display literal for the value, quoted unless the column is numeric. (Execution always
    /// uses a parameter — this string is display-only.)</summary>
    internal static string Literal(string value, string? dataType)
    {
        var t = (dataType ?? "").ToLowerInvariant();
        var numeric = t.Contains("int") || t.Contains("decimal") || t.Contains("numeric")
            || t.Contains("float") || t.Contains("real") || t.Contains("money") || t.Contains("double")
            || t.Contains("bit") || t.Contains("byte") || t.Contains("short") || t.Contains("long");
        if (numeric && value.Length > 0) return value;
        return "'" + value.Replace("'", "''") + "'";
    }

    private static object? Normalize(object v) => v switch
    {
        null or DBNull => null,
        DateTime dt => dt.ToString("o"),
        byte[] => "(binary)",
        _ => v,
    };

    private static List<SchemaColumnInfo> DeserializeColumns(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try { return JsonSerializer.Deserialize<List<SchemaColumnInfo>>(json, JsonOpts) ?? []; }
        catch { return []; }
    }
}
