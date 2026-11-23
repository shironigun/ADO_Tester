using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QaAutomation.Api.Streaming;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Contracts;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Data;
using QaAutomation.Infrastructure.SchemaExplorer;

namespace QaAutomation.Api.Controllers;

/// <summary>
/// DB Schema Explorer API. Manages scan-target products; the one-time repo scan, the schema
/// graph/mindmap, the entity registry, and live entity tracing land on this controller in later
/// phases. Everything here is read-only with respect to the scanned product.
/// </summary>
[ApiController]
[Route("api/schema")]
public sealed class SchemaController : ControllerBase
{
    private readonly QaDbContext _db;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEventBus _bus;
    private readonly SchemaTraceService _trace;
    private readonly IOptionsMonitor<AzureDevOpsOptions> _adoOpts;
    private readonly ILogger<SchemaController> _log;

    public SchemaController(QaDbContext db, IServiceScopeFactory scopeFactory, IEventBus bus,
        SchemaTraceService trace, IOptionsMonitor<AzureDevOpsOptions> adoOpts, ILogger<SchemaController> log)
    {
        _db = db;
        _scopeFactory = scopeFactory;
        _bus = bus;
        _trace = trace;
        _adoOpts = adoOpts;
        _log = log;
    }

    // ── Current product (single, sourced from Azure DevOps settings) ───────────────

    /// <summary>The one Schema Explorer product, derived from the global Azure DevOps settings
    /// (org/project/repo/branch). Creates it on first use and keeps its ADO fields in sync.</summary>
    [HttpGet("product")]
    public async Task<IActionResult> GetCurrentProduct(CancellationToken ct)
    {
        try
        {
            var o = _adoOpts.CurrentValue;
            var branch = string.IsNullOrWhiteSpace(o.DefaultBranch) ? "main" : o.DefaultBranch;
            var name = !string.IsNullOrWhiteSpace(o.Repository) ? o.Repository
                : (!string.IsNullOrWhiteSpace(o.Project) ? o.Project : "Product");
            var now = DateTime.UtcNow;

            var product = await _db.SchemaProducts.OrderBy(p => p.Id).FirstOrDefaultAsync(ct);
            if (product is null)
            {
                product = new SchemaProduct
                {
                    Name = name, AdoProject = o.Project ?? "", AdoRepository = o.Repository ?? "",
                    DefaultBranch = branch, CreatedAt = now, UpdatedAt = now,
                };
                _db.SchemaProducts.Add(product);
                await _db.SaveChangesAsync(ct);
            }
            else
            {
                var changed = false;
                if (product.AdoProject != (o.Project ?? "")) { product.AdoProject = o.Project ?? ""; changed = true; }
                if (product.AdoRepository != (o.Repository ?? "")) { product.AdoRepository = o.Repository ?? ""; changed = true; }
                if (product.DefaultBranch != branch) { product.DefaultBranch = branch; changed = true; }
                if (product.Name != name) { product.Name = name; changed = true; }
                if (changed) { product.UpdatedAt = now; await _db.SaveChangesAsync(ct); }
            }

            var latest = await LatestScanAsync(product.Id, ct);
            return Ok(SchemaProductDto.From(product, latest));
        }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    // ── Products ────────────────────────────────────────────────────────────────

    [HttpGet("products")]
    public async Task<IActionResult> ListProducts(CancellationToken ct)
    {
        try
        {
            var products = await _db.SchemaProducts.OrderBy(p => p.Name).ToListAsync(ct);
            var latest = await LatestScansAsync(products.Select(p => p.Id).ToList(), ct);
            var items = products
                .Select(p => SchemaProductDto.From(p, latest.GetValueOrDefault(p.Id)))
                .ToList();
            return Ok(new SchemaProductList { Total = items.Count, Items = items });
        }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpGet("products/{id:int}")]
    public async Task<IActionResult> GetProduct(int id, CancellationToken ct)
    {
        try
        {
            var product = await _db.SchemaProducts.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (product is null) return NotFound(new { detail = $"Product {id} not found." });
            var latest = await LatestScanAsync(id, ct);
            return Ok(SchemaProductDto.From(product, latest));
        }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpPost("products")]
    public async Task<IActionResult> CreateProduct([FromBody] CreateProductRequest req, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.AdoProject)
                || string.IsNullOrWhiteSpace(req.AdoRepository))
                return BadRequest(new { detail = "Name, AdoProject and AdoRepository are required." });

            var now = DateTime.UtcNow;
            var product = new SchemaProduct
            {
                Name = req.Name.Trim(),
                AdoProject = req.AdoProject.Trim(),
                AdoRepository = req.AdoRepository.Trim(),
                DefaultBranch = string.IsNullOrWhiteSpace(req.DefaultBranch) ? "main" : req.DefaultBranch.Trim(),
                Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim(),
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.SchemaProducts.Add(product);
            await _db.SaveChangesAsync(ct);
            return StatusCode(201, SchemaProductDto.From(product));
        }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpPatch("products/{id:int}")]
    public async Task<IActionResult> UpdateProduct(int id, [FromBody] UpdateProductRequest req, CancellationToken ct)
    {
        try
        {
            var product = await _db.SchemaProducts.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (product is null) return NotFound(new { detail = $"Product {id} not found." });

            if (req.Name is { } n && !string.IsNullOrWhiteSpace(n)) product.Name = n.Trim();
            if (req.AdoProject is { } ap && !string.IsNullOrWhiteSpace(ap)) product.AdoProject = ap.Trim();
            if (req.AdoRepository is { } ar && !string.IsNullOrWhiteSpace(ar)) product.AdoRepository = ar.Trim();
            if (req.DefaultBranch is { } br && !string.IsNullOrWhiteSpace(br)) product.DefaultBranch = br.Trim();
            if (req.Description is { } d) product.Description = string.IsNullOrWhiteSpace(d) ? null : d.Trim();
            product.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync(ct);
            var latest = await LatestScanAsync(id, ct);
            return Ok(SchemaProductDto.From(product, latest));
        }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpDelete("products/{id:int}")]
    public async Task<IActionResult> DeleteProduct(int id, CancellationToken ct)
    {
        try
        {
            var product = await _db.SchemaProducts.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (product is null) return NotFound(new { detail = $"Product {id} not found." });
            _db.SchemaProducts.Remove(product);   // cascades scans → graph rows, entities, connections
            await _db.SaveChangesAsync(ct);
            return NoContent();
        }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    /// <summary>The AI-proposed (+ any user-curated) entity registry for a product — the roots a live
    /// trace can start from.</summary>
    [HttpGet("products/{id:int}/entities")]
    public async Task<IActionResult> ListEntities(int id, CancellationToken ct)
    {
        try
        {
            var exists = await _db.SchemaProducts.AnyAsync(p => p.Id == id, ct);
            if (!exists) return NotFound(new { detail = $"Product {id} not found." });
            var entities = await _db.SchemaEntities.Where(e => e.ProductId == id)
                .OrderBy(e => e.SortOrder).ThenBy(e => e.DisplayName).ToListAsync(ct);
            // DisplayColumnsJson is a plain string[] (not columns).
            var items = entities.Select(e => SchemaEntityDto.From(e, DeserializeStringList(e.DisplayColumnsJson))).ToList();
            return Ok(new SchemaEntityList { Total = items.Count, Items = items });
        }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    private static IReadOnlyList<string> DeserializeStringList(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json, WebJson) ?? []; }
        catch { return []; }
    }

    // ── Live trace / read-only connection ─────────────────────────────────────────

    [HttpGet("products/{id:int}/connections")]
    public async Task<IActionResult> ListConnections(int id, CancellationToken ct)
    {
        try
        {
            var items = await _db.SchemaConnections
                .Where(c => c.ProductId == id && c.ServiceName == null)
                .OrderByDescending(c => c.IsDefault).ThenBy(c => c.Environment)
                .ToListAsync(ct);
            return Ok(new ConnectionList
            {
                Items = items.Select(c => new EnvironmentConnection
                { Environment = c.Environment, Server = c.Server, User = c.DbUser, Database = c.DbDatabase }).ToList(),
            });
        }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpPut("products/{id:int}/connections")]
    public async Task<IActionResult> SetConnection(int id, [FromBody] SetConnectionRequest req, CancellationToken ct)
    {
        var env = string.IsNullOrWhiteSpace(req.Environment) ? "Default" : req.Environment.Trim();
        if (string.IsNullOrWhiteSpace(req.Server) || string.IsNullOrWhiteSpace(req.User) || string.IsNullOrWhiteSpace(req.Database))
            return BadRequest(new { detail = "Server, User and Database are required." });

        var product = await _db.SchemaProducts.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (product is null) return NotFound(new { detail = $"Product {id} not found." });

        var now = DateTime.UtcNow;
        var existing = await ConnectionFor(id, env, ct);

        // Blank password when editing = keep the current one (parsed from the stored string).
        var password = req.Password;
        if (string.IsNullOrEmpty(password))
        {
            if (existing is null) return BadRequest(new { detail = "Password is required for a new environment." });
            password = ExistingPassword(existing.ConnectionString);
        }

        string connString;
        try { connString = BuildConnectionString(req.Server.Trim(), req.User.Trim(), password!, req.Database.Trim()); }
        catch (Exception e) { return BadRequest(new { detail = $"Invalid connection details: {e.Message}" }); }

        if (existing is null)
        {
            var isFirst = !await _db.SchemaConnections.AnyAsync(c => c.ProductId == id && c.ServiceName == null, ct);
            _db.SchemaConnections.Add(new SchemaConnection
            {
                ProductId = id, Environment = env, ServiceName = null,
                Server = req.Server.Trim(), DbUser = req.User.Trim(), DbDatabase = req.Database.Trim(),
                ConnectionString = connString, IsDefault = isFirst, CreatedAt = now, UpdatedAt = now,
            });
        }
        else
        {
            existing.Server = req.Server.Trim();
            existing.DbUser = req.User.Trim();
            existing.DbDatabase = req.Database.Trim();
            existing.ConnectionString = connString;
            existing.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new EnvironmentConnection { Environment = env, Server = req.Server.Trim(), User = req.User.Trim(), Database = req.Database.Trim() });
    }

    [HttpDelete("products/{id:int}/connections/{environment}")]
    public async Task<IActionResult> DeleteConnection(int id, string environment, CancellationToken ct)
    {
        try
        {
            var existing = await ConnectionFor(id, environment, ct);
            if (existing is not null) { _db.SchemaConnections.Remove(existing); await _db.SaveChangesAsync(ct); }
            return NoContent();
        }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpPost("products/{id:int}/connections/test")]
    public async Task<IActionResult> TestConnection(int id, [FromBody] TestConnectionRequest? req, CancellationToken ct)
    {
        string? cs = null;
        if (!string.IsNullOrWhiteSpace(req?.Server) && !string.IsNullOrWhiteSpace(req?.User) && !string.IsNullOrWhiteSpace(req?.Database))
        {
            // Blank password + a named environment = reuse that environment's saved password.
            var pwd = req!.Password;
            if (string.IsNullOrEmpty(pwd) && !string.IsNullOrWhiteSpace(req.Environment))
                pwd = ExistingPassword((await ConnectionFor(id, req.Environment!.Trim(), ct))?.ConnectionString);
            try { cs = BuildConnectionString(req.Server!.Trim(), req.User!.Trim(), pwd ?? "", req.Database!.Trim()); }
            catch (Exception e) { return Ok(new { ok = false, error = $"Invalid connection details: {e.Message}" }); }
        }
        else if (!string.IsNullOrWhiteSpace(req?.Environment))
        {
            cs = (await ConnectionFor(id, req!.Environment!.Trim(), ct))?.ConnectionString;
        }
        if (string.IsNullOrWhiteSpace(cs)) return BadRequest(new { detail = "Provide Server/User/Database (and password) or an existing environment to test." });
        try { await _trace.TestConnectionAsync(cs, ct); return Ok(new { ok = true }); }
        catch (Exception e) { return Ok(new { ok = false, error = e.Message }); }
    }

    [HttpPost("products/{id:int}/trace")]
    public async Task<IActionResult> Trace(int id, [FromBody] TraceRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.EntityKey) || string.IsNullOrWhiteSpace(req.Id))
            return BadRequest(new { detail = "entityKey and id are required." });
        try { return Ok(await _trace.TraceAsync(id, req.EntityKey.Trim(), req.Id.Trim(), req.Environment?.Trim(), ct)); }
        catch (TraceException e) { return BadRequest(new { detail = e.Message }); }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    private async Task<SchemaConnection?> ConnectionFor(int productId, string environment, CancellationToken ct) =>
        await _db.SchemaConnections.FirstOrDefaultAsync(
            c => c.ProductId == productId && c.ServiceName == null && c.Environment == environment, ct);

    /// <summary>Build a SQL Server connection string from discrete fields. Encrypt + TrustServerCertificate
    /// so it works against internal servers with self-signed certs.</summary>
    private static string BuildConnectionString(string server, string user, string password, string database) =>
        new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            UserID = user,
            Password = password,
            Encrypt = true,
            TrustServerCertificate = true,
            ConnectTimeout = 15,
        }.ConnectionString;

    private static string ExistingPassword(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return "";
        try { return new SqlConnectionStringBuilder(connectionString).Password; }
        catch { return ""; }
    }

    // ── Scans ─────────────────────────────────────────────────────────────────────

    [HttpGet("products/{id:int}/scans")]
    public async Task<IActionResult> ListScans(int id, CancellationToken ct)
    {
        try
        {
            var exists = await _db.SchemaProducts.AnyAsync(p => p.Id == id, ct);
            if (!exists) return NotFound(new { detail = $"Product {id} not found." });
            var scans = await _db.SchemaScans.Where(s => s.ProductId == id)
                .OrderByDescending(s => s.CreatedAt).ToListAsync(ct);
            var items = scans.Select(SchemaScanDto.From).ToList();
            return Ok(new SchemaScanList { Total = items.Count, Items = items });
        }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    /// <summary>Kick off a read-only inventory scan of the product's repo. Seeds the scan row, then
    /// runs Phase A on a background Task with its own DI scope; progress streams over SSE.</summary>
    [HttpPost("products/{id:int}/scan")]
    public async Task<IActionResult> StartScan(int id, CancellationToken ct)
    {
        var exists = await _db.SchemaProducts.AnyAsync(p => p.Id == id, ct);
        if (!exists) return NotFound(new { detail = $"Product {id} not found." });

        var scan = new SchemaScan { ProductId = id, Status = SchemaScanStatus.Queued, CreatedAt = DateTime.UtcNow };
        _db.SchemaScans.Add(scan);
        await _db.SaveChangesAsync(ct);
        var scanId = scan.Id;

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<SchemaScanService>();
                await svc.ExecuteAsync(scanId);
            }
            catch (Exception e) { _log.LogError(e, "Schema scan {ScanId} background task failed", scanId); }
        });

        return StatusCode(202, new { scanId });
    }

    /// <summary>Live SSE progress for a scan (reuses the shared EventBus + SseWriter).</summary>
    [HttpGet("scans/{scanId:int}/stream")]
    public async Task StreamScan(int scanId, [FromQuery] string? lastEventId, CancellationToken ct)
    {
        var afterSeq = SseWriter.ParseLastEventId(Request.Headers["Last-Event-ID"].FirstOrDefault(), lastEventId);
        await SseWriter.StreamAsync(Response, _bus, SchemaScanService.EventKey(scanId), afterSeq, ct);
    }

    /// <summary>The schema graph (services → tables → relations) built by Phase B.</summary>
    [HttpGet("scans/{scanId:int}/graph")]
    public async Task<IActionResult> GetGraph(int scanId, CancellationToken ct)
    {
        try
        {
            var exists = await _db.SchemaScans.AnyAsync(s => s.Id == scanId, ct);
            if (!exists) return NotFound(new { detail = $"Scan {scanId} not found." });

            var services = await _db.SchemaServices.Where(s => s.ScanId == scanId)
                .OrderBy(s => s.SortOrder).ToListAsync(ct);
            var tables = await _db.SchemaTables.Where(t => t.ScanId == scanId).ToListAsync(ct);
            var relations = await _db.SchemaRelations.Where(r => r.ScanId == scanId).ToListAsync(ct);

            var tableCountByService = tables.Where(t => t.ServiceId is not null)
                .GroupBy(t => t.ServiceId!.Value).ToDictionary(g => g.Key, g => g.Count());

            var graph = new SchemaGraphDto
            {
                ScanId = scanId,
                Services = services.Select(s => SchemaServiceDto.From(s, tableCountByService.GetValueOrDefault(s.Id))).ToList(),
                Tables = tables.Select(t => new SchemaTableDto
                {
                    Id = t.Id, ServiceId = t.ServiceId, Name = t.Name, SqlSchema = t.SqlSchema,
                    IsKeyless = t.IsKeyless, Columns = DeserializeColumns(t.ColumnsJson),
                }).ToList(),
                Relations = relations.Select(SchemaRelationDto.From).ToList(),
            };
            return Ok(graph);
        }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    private static IReadOnlyList<SchemaColumnInfo> DeserializeColumns(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try { return JsonSerializer.Deserialize<List<SchemaColumnInfo>>(json, WebJson) ?? []; }
        catch { return []; }
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    /// <summary>Scan detail with the parsed inventory (bucket → file paths) from Phase A.</summary>
    [HttpGet("scans/{scanId:int}")]
    public async Task<IActionResult> GetScan(int scanId, CancellationToken ct)
    {
        try
        {
            var scan = await _db.SchemaScans.FirstOrDefaultAsync(s => s.Id == scanId, ct);
            if (scan is null) return NotFound(new { detail = $"Scan {scanId} not found." });

            object? inventory = null;
            if (scan.StatsJson is { Length: > 0 } j)
                try { inventory = JsonSerializer.Deserialize<JsonElement>(j); }
                catch { /* malformed stats → omit inventory */ }

            return Ok(new { scan = SchemaScanDto.From(scan), inventory });
        }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private async Task<SchemaScan?> LatestScanAsync(int productId, CancellationToken ct) =>
        await _db.SchemaScans.Where(s => s.ProductId == productId)
            .OrderByDescending(s => s.CreatedAt).FirstOrDefaultAsync(ct);

    private async Task<Dictionary<int, SchemaScan>> LatestScansAsync(IReadOnlyList<int> productIds, CancellationToken ct)
    {
        if (productIds.Count == 0) return [];
        // One query, newest-first; keep the first (latest) scan seen per product.
        var scans = await _db.SchemaScans.Where(s => productIds.Contains(s.ProductId))
            .OrderByDescending(s => s.CreatedAt).ToListAsync(ct);
        var map = new Dictionary<int, SchemaScan>();
        foreach (var s in scans) map.TryAdd(s.ProductId, s);
        return map;
    }
}
