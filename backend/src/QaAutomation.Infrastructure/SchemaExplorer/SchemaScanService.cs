using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Agents;
using QaAutomation.Infrastructure.Data;

namespace QaAutomation.Infrastructure.SchemaExplorer;

/// <summary>
/// The DB Schema Explorer scan, READ-ONLY end to end. Phase A inventories the product's ADO repo
/// (list tree → classify files → HEAD commit). Phase B parses the EF DbContexts + entity classes into
/// the schema graph (services → tables → relations). AI relation inference + the entity registry are
/// Phase C. Progress streams over the shared EventBus/SSE. Scoped — runs in a background DI scope.
/// </summary>
public sealed class SchemaScanService
{
    private const int FetchConcurrency = 8;

    private readonly QaDbContext _db;
    private readonly IAzureDevOpsClient _ado;
    private readonly IEventBus _bus;
    private readonly ILlmClientFactory _llmFactory;
    private readonly ILogger<SchemaScanService> _log;

    public SchemaScanService(QaDbContext db, IAzureDevOpsClient ado, IEventBus bus,
        ILlmClientFactory llmFactory, ILogger<SchemaScanService> log)
    {
        _db = db;
        _ado = ado;
        _bus = bus;
        _llmFactory = llmFactory;
        _log = log;
    }

    public static string EventKey(int scanId) => $"schema-scan:{scanId}";

    // camelCase to match the rest of the API — StatsJson/ColumnsJson are returned to the client verbatim
    // (as JsonElement), so PascalCase here would leak wrong-cased keys onto the wire.
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>The inventory persisted to <see cref="SchemaScan.StatsJson"/>. Bucket → file paths;
    /// unclassified files are counted only (keeps the blob bounded).</summary>
    public sealed record Inventory(
        string? CommitId, string Branch, int TotalFiles, int OtherCount,
        Dictionary<string, List<string>> Buckets);

    public async Task ExecuteAsync(int scanId, CancellationToken ct = default)
    {
        var key = EventKey(scanId);
        var scan = await _db.SchemaScans.FirstOrDefaultAsync(s => s.Id == scanId, ct);
        if (scan is null) { _log.LogWarning("SchemaScan {ScanId} vanished before execution", scanId); return; }
        var product = await _db.SchemaProducts.FirstOrDefaultAsync(p => p.Id == scan.ProductId, ct);
        if (product is null) { await FailAsync(scan, key, "Product no longer exists.", ct); return; }

        try
        {
            scan.Status = SchemaScanStatus.Running;
            scan.Phase = "Inventory";
            scan.StartedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            Pub(key, EventKinds.Phase, $"Scanning {product.Name} — {product.AdoProject}/{product.AdoRepository} @ {product.DefaultBranch}");

            // ponytail: uses the client's configured ADO org/project; works because the scan target
            // repo lives under the configured project. Per-product ADO org/project config is a later task.
            var repoId = await _ado.ResolveRepositoryIdAsync(product.AdoRepository, ct);
            Pub(key, EventKinds.System, "Listing repository tree…");

            var files = await _ado.ListRepoFilePathsAsync("/", product.DefaultBranch, repoId, ct);
            Pub(key, EventKinds.Stat, $"{files.Count} files found", data: new() { ["totalFiles"] = files.Count });

            string? commitId = null;
            try { commitId = (await _ado.GetCommitsForPathAsync("/", 1, repoId, ct)).FirstOrDefault(); }
            catch (Exception e) { _log.LogDebug(e, "commit freshness lookup failed (non-fatal)"); }

            var buckets = new Dictionary<string, List<string>>();
            var other = 0;
            foreach (var path in files)
            {
                var bucket = Classify(path);
                if (bucket is null) { other++; continue; }
                if (!buckets.TryGetValue(bucket, out var list)) buckets[bucket] = list = [];
                list.Add(path);
            }

            foreach (var (name, list) in buckets.OrderByDescending(b => b.Value.Count))
                Pub(key, EventKinds.System, $"  {name}: {list.Count}", level: EventLevels.Success);

            var inventory = new Inventory(commitId, product.DefaultBranch, files.Count, other, buckets);
            scan.StatsJson = JsonSerializer.Serialize(inventory, JsonOpts);
            scan.CommitId = commitId;
            scan.Branch = product.DefaultBranch;
            await _db.SaveChangesAsync(ct);
            Pub(key, EventKinds.Result,
                $"Inventory: {buckets.Values.Sum(v => v.Count)} schema-relevant files across {buckets.Count} categories.",
                level: EventLevels.Success);

            // ── Phase B: deterministic EF parse → schema graph ─────────────────
            var (services, tables, relations) = await RunParseAsync(scan, product, repoId, inventory, key, ct);

            // ── Phase C: infer implicit relations + propose the entity registry ─
            var (inferred, entities) = await RunInferAsync(scan, product, key, ct);

            scan.ServiceCount = services;
            scan.TableCount = tables;
            scan.RelationCount = relations + inferred;
            scan.EntityCount = entities;
            scan.Status = SchemaScanStatus.Succeeded;
            scan.Phase = null;
            scan.CompletedAt = DateTime.UtcNow;
            scan.EventLogJson = SerializeNarrative(key);
            await _db.SaveChangesAsync(ct);

            Pub(key, EventKinds.Result,
                $"Scan complete — {services} services, {tables} tables, {relations + inferred} relations "
                + $"({inferred} inferred), {entities} entities.",
                level: EventLevels.Success, terminal: true);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Schema scan {ScanId} failed", scanId);
            await FailAsync(scan, key, e.Message, ct);
        }
    }

    // ── Phase B ─────────────────────────────────────────────────────────────────

    private async Task<(int services, int tables, int relations)> RunParseAsync(
        SchemaScan scan, SchemaProduct product, string repoId, Inventory inv, string key, CancellationToken ct)
    {
        scan.Phase = "Parse";
        await _db.SaveChangesAsync(ct);
        Pub(key, EventKinds.Phase, "Parsing EF schema (DbContexts + entity classes)…");

        var branch = product.DefaultBranch;
        var contextPaths = inv.Buckets.GetValueOrDefault("dbContexts") ?? [];
        var entityPaths = inv.Buckets.GetValueOrDefault("entities") ?? [];

        // 1. Fetch + parse DbContexts (also harvest inline entity classes declared in them).
        var contextContents = await FetchAllAsync(contextPaths, branch, repoId, key, ct);
        var classMap = new Dictionary<string, EfSchemaParser.ParsedClass>(StringComparer.Ordinal);
        var contexts = new List<(string Path, EfSchemaParser.DbContextInfo Info)>();
        foreach (var (path, content) in contextContents)
        {
            contexts.Add((path, EfSchemaParser.ParseDbContext(content)));
            foreach (var c in EfSchemaParser.ParseClasses(content)) classMap.TryAdd(c.ClassName, c);
        }

        // 2. Referenced DbSet types = the tables we must resolve.
        var knownTables = contexts.SelectMany(c => c.Info.DbSetTypes).ToHashSet(StringComparer.Ordinal);

        // 3. Fetch entity files by basename==typeName for types not already parsed inline, then harvest
        //    EVERY class in each fetched file (co-located classes come free).
        // ponytail: basename match only — oddly-named/unfound types get a columnless node (still shown),
        //    Phase C enriches. Avoids fetching all entity files.
        var byBase = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var p in entityPaths)
        {
            var b = Path.GetFileNameWithoutExtension(p);
            if (!byBase.TryGetValue(b, out var l)) byBase[b] = l = [];
            l.Add(p);
        }
        var toFetch = knownTables.Where(t => !classMap.ContainsKey(t))
            .SelectMany(t => byBase.GetValueOrDefault(t) ?? [])
            .Distinct().ToList();
        var entityContents = await FetchAllAsync(toFetch, branch, repoId, key, ct);
        foreach (var content in entityContents.Values)
            foreach (var c in EfSchemaParser.ParseClasses(content)) classMap.TryAdd(c.ClassName, c);

        Pub(key, EventKinds.System,
            $"Parsed {classMap.Count} classes from {contexts.Count} DbContexts + {entityContents.Count} entity files.");

        // 4. Assemble the graph — one service per DbContext.
        int svcCount = 0, tblCount = 0, relCount = 0, order = 0;
        foreach (var (path, info) in contexts)
        {
            var service = new SchemaService
            {
                ScanId = scan.Id,
                Name = FirstSegment(path),
                DbContextClass = info.ClassName,
                SourcePath = path,
                SortOrder = order++,
            };
            _db.SchemaServices.Add(service);
            await _db.SaveChangesAsync(ct);
            svcCount++;

            // Tables for this context.
            var tableRows = new List<(string Type, SchemaTable Row)>();
            foreach (var type in info.DbSetTypes)
            {
                classMap.TryGetValue(type, out var cls);
                var composite = info.CompositeKeys.GetValueOrDefault(type);
                var columns = BuildColumns(cls, knownTables, composite);
                var row = new SchemaTable
                {
                    ScanId = scan.Id,
                    ServiceId = service.Id,
                    Name = cls?.TableName ?? type,
                    SqlSchema = cls?.Schema,
                    IsKeyless = info.HasNoKey.Contains(type),
                    ColumnsJson = columns.Count > 0 ? JsonSerializer.Serialize(columns, JsonOpts) : null,
                    CreatedAt = DateTime.UtcNow,
                };
                _db.SchemaTables.Add(row);
                tableRows.Add((type, row));
            }
            await _db.SaveChangesAsync(ct);   // assigns table ids
            tblCount += tableRows.Count;

            var tableIdByType = tableRows.ToDictionary(r => r.Type, r => r.Row.Id, StringComparer.Ordinal);

            // Relations from navigation properties, resolved within this service.
            var seen = new HashSet<string>();
            foreach (var type in info.DbSetTypes)
            {
                if (!classMap.TryGetValue(type, out var cls)) continue;
                if (!tableIdByType.TryGetValue(type, out var typeId)) continue;
                foreach (var prop in cls.Props)
                {
                    var elem = EfSchemaParser.CollectionElement(prop.RawType);
                    if (elem is not null)
                    {
                        // Collection nav: the child (elem) holds the FK → edge elem → type.
                        if (tableIdByType.TryGetValue(elem, out var childId))
                            AddRelation(childId, typeId, prop.FkColumn);
                    }
                    else if (!EfSchemaParser.IsScalar(prop.RawType))
                    {
                        // Reference nav: this type holds the FK → edge type → target.
                        var target = LastSegment(prop.RawType);
                        if (tableIdByType.TryGetValue(target, out var tgtId))
                            AddRelation(typeId, tgtId, prop.FkColumn);
                    }
                }
            }
            await _db.SaveChangesAsync(ct);

            Pub(key, EventKinds.System,
                $"  {service.Name}: {tableRows.Count} tables", level: EventLevels.Success);

            void AddRelation(int src, int tgt, string? col)
            {
                if (src == tgt) return;
                if (!seen.Add($"{src}->{tgt}:{col}")) return;
                _db.SchemaRelations.Add(new SchemaRelation
                {
                    ScanId = scan.Id,
                    SourceTableId = src,
                    TargetTableId = tgt,
                    SourceColumn = col,
                    Kind = col is null ? SchemaRelationKind.EfNavigation : SchemaRelationKind.ForeignKey,
                    CreatedAt = DateTime.UtcNow,
                });
                relCount++;
            }
        }

        return (svcCount, tblCount, relCount);
    }

    // ── Phase C ─────────────────────────────────────────────────────────────────

    private async Task<(int inferred, int entities)> RunInferAsync(
        SchemaScan scan, SchemaProduct product, string key, CancellationToken ct)
    {
        scan.Phase = "Infer";
        await _db.SaveChangesAsync(ct);
        Pub(key, EventKinds.Phase, "Inferring implicit relations + proposing the entity registry…");

        var tables = await _db.SchemaTables.Where(t => t.ScanId == scan.Id).ToListAsync(ct);
        var existing = await _db.SchemaRelations.Where(r => r.ScanId == scan.Id).ToListAsync(ct);
        var svcName = await _db.SchemaServices.Where(s => s.ScanId == scan.Id)
            .ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        var inferred = InferImplicitRelations(scan.Id, tables, existing, svcName);
        await _db.SaveChangesAsync(ct);
        Pub(key, EventKinds.System, $"  inferred {inferred} implicit (cross-service) relations", level: EventLevels.Success);

        var entities = 0;
        try { entities = await ProposeEntitiesAsync(scan, product, tables, key, ct); }
        catch (Exception e)
        {
            _log.LogWarning(e, "Entity registry proposal failed (non-fatal) for scan {ScanId}", scan.Id);
            Pub(key, EventKinds.System, "  entity registry skipped (LLM unavailable)", level: EventLevels.Warn);
        }
        return (inferred, entities);
    }

    /// <summary>Deterministic implicit-FK inference: a scalar <c>&lt;Entity&gt;Id</c> column that isn't
    /// already a PK/explicit-FK points at the keyed table named <c>&lt;Entity&gt;</c> (or one whose PK is
    /// exactly that column). Cross-service DbSet reuse yields several tables with the same name, so each
    /// name resolves to ONE canonical table (prefer the service that owns it) — keeps the edge to the
    /// entity, not to every read-replica copy. These are the dashed cross-service edges; no LLM needed.</summary>
    private int InferImplicitRelations(
        int scanId, List<SchemaTable> tables, List<SchemaRelation> existing, Dictionary<int, string> svcName)
    {
        var colsByTable = tables.ToDictionary(t => t.Id, t => DeserializeColumns(t.ColumnsJson));

        var keyedByName = new Dictionary<string, List<SchemaTable>>(StringComparer.OrdinalIgnoreCase);
        var pkOwner = new Dictionary<string, SchemaTable>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tables)
        {
            if (t.IsKeyless) continue;
            if (!keyedByName.TryGetValue(t.Name, out var nl)) keyedByName[t.Name] = nl = [];
            nl.Add(t);
            foreach (var c in colsByTable[t.Id].Where(c => c.IsPrimaryKey))
                pkOwner.TryAdd(c.Name, t);
        }

        // One canonical table per name: prefer the service whose name matches the table (the owner),
        // else the lowest id. Dedups Dealer×4 → the dealer service's Dealer.
        var canonical = new Dictionary<string, SchemaTable>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, group) in keyedByName)
            canonical[name] = group.FirstOrDefault(t =>
                    svcName.GetValueOrDefault(t.ServiceId ?? -1, "").Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? group.OrderBy(t => t.Id).First();

        var existingPairs = existing.Select(r => (r.SourceTableId, r.TargetTableId)).ToHashSet();
        var seen = new HashSet<(int, int, string)>();
        var count = 0;

        foreach (var t in tables)
        {
            foreach (var col in colsByTable[t.Id])
            {
                if (col.IsPrimaryKey || col.IsForeignKey || !EndsWithId(col.Name)) continue;
                var baseName = col.Name[..^2];   // strip "Id"/"ID"
                if (baseName.Length < 2) continue;

                SchemaTable? target;
                double confidence;
                if (canonical.TryGetValue(baseName, out var byName))
                {
                    target = byName;
                    confidence = pkOwner.ContainsKey(col.Name) ? 0.85 : 0.6;
                }
                else if (pkOwner.TryGetValue(col.Name, out var byPk))
                {
                    target = byPk;   // column is exactly another table's PK
                    confidence = 0.85;
                }
                else continue;

                if (target.Id == t.Id || existingPairs.Contains((t.Id, target.Id))) continue;
                if (!seen.Add((t.Id, target.Id, col.Name))) continue;
                _db.SchemaRelations.Add(new SchemaRelation
                {
                    ScanId = scanId,
                    SourceTableId = t.Id,
                    TargetTableId = target.Id,
                    SourceColumn = col.Name,
                    Kind = SchemaRelationKind.Inferred,
                    Confidence = confidence,
                    Evidence = $"{col.Name} → {target.Name} (id-column convention)",
                    CreatedAt = DateTime.UtcNow,
                });
                count++;
            }
        }
        return count;
    }

    /// <summary>One LLM call proposes the user-meaningful entity registry over the keyed tables. Replaces
    /// prior AI proposals (source="ai") for the product; user-curated entries are preserved.</summary>
    private async Task<int> ProposeEntitiesAsync(
        SchemaScan scan, SchemaProduct product, List<SchemaTable> tables, string key, CancellationToken ct)
    {
        var svcName = await _db.SchemaServices.Where(s => s.ScanId == scan.Id)
            .ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        var catalog = new List<TableCatalogEntry>();
        foreach (var t in tables.Where(t => !t.IsKeyless))
        {
            var cols = DeserializeColumns(t.ColumnsJson);
            var pk = string.Join("+", cols.Where(c => c.IsPrimaryKey).Select(c => c.Name));
            var preview = string.Join(", ", cols.Take(8).Select(c => c.Name));
            catalog.Add(new TableCatalogEntry(
                t.ServiceId is { } sid ? svcName.GetValueOrDefault(sid, "") : "",
                t.Name, pk.Length > 0 ? pk : "-", preview));
        }
        if (catalog.Count == 0) return 0;

        Pub(key, EventKinds.System, $"  asking the model to name entities from {catalog.Count} keyed tables…");
        var llm = await _llmFactory.CreateAsync(ct);
        var proposals = await new SchemaEntityAgent(llm).ProposeAsync(product.Name, catalog, ct);

        // Replace prior AI proposals; keep user-curated. Two SaveChanges so the delete lands before the
        // re-insert (the unique (ProductId, Key) index would otherwise clash within one transaction).
        var old = await _db.SchemaEntities.Where(e => e.ProductId == product.Id && e.Source == "ai").ToListAsync(ct);
        _db.SchemaEntities.RemoveRange(old);
        await _db.SaveChangesAsync(ct);

        var curated = (await _db.SchemaEntities.Where(e => e.ProductId == product.Id).Select(e => e.Key).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var order = 0;
        var added = 0;
        foreach (var p in proposals)
        {
            var k = Kebab(p.Key.Length > 0 ? p.Key : p.DisplayName);
            if (k.Length == 0 || curated.Contains(k) || !seen.Add(k)) continue;
            _db.SchemaEntities.Add(new SchemaEntity
            {
                ProductId = product.Id,
                Key = k,
                DisplayName = p.DisplayName.Length > 0 ? p.DisplayName : k,
                RootTableName = LastSegment(p.RootTable),   // AI sometimes qualifies (service.Table) — store bare

                IdColumn = p.IdColumn,
                ScopeColumn = p.ScopeColumn,
                DisplayColumnsJson = SerializeDisplayCols(p.DisplayColumns),
                Confidence = ConfToNum(p.Confidence),
                Source = "ai",
                IsEnabled = true,
                SortOrder = order++,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            added++;
        }
        await _db.SaveChangesAsync(ct);
        Pub(key, EventKinds.System, $"  proposed {added} entities", level: EventLevels.Success);
        return added;
    }

    private static bool EndsWithId(string name) =>
        name.Length > 2 && (name.EndsWith("Id", StringComparison.Ordinal) || name.EndsWith("ID", StringComparison.Ordinal));

    private static List<SchemaColumnInfo> DeserializeColumns(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try { return JsonSerializer.Deserialize<List<SchemaColumnInfo>>(json, JsonOpts) ?? []; }
        catch { return []; }
    }

    private static string? SerializeDisplayCols(string csv)
    {
        var cols = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return cols.Length == 0 ? null : JsonSerializer.Serialize(cols, JsonOpts);
    }

    private static double ConfToNum(string c) => c.ToLowerInvariant() switch
    {
        "high" => 0.9, "medium" => 0.6, "low" => 0.3, _ => 0.6,
    };

    private static string Kebab(string s)
    {
        var chars = s.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Scalar columns for a table (nav properties become relations, not columns). Marks PK from
    /// [Key]/composite HasKey and FK from nav [ForeignKey] columns that point at a known table.</summary>
    private static List<SchemaColumnInfo> BuildColumns(
        EfSchemaParser.ParsedClass? cls, HashSet<string> knownTables, List<string>? composite)
    {
        if (cls is null) return [];

        // Only REFERENCE navs put an FK column on THIS table. A [ForeignKey] on a collection nav names
        // the FK on the child table, not a column here — so skip collections.
        var fkToTarget = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in cls.Props)
        {
            if (p.FkColumn is null || EfSchemaParser.CollectionElement(p.RawType) is not null) continue;
            if (EfSchemaParser.IsScalar(p.RawType)) continue;
            var target = LastSegment(p.RawType);
            if (knownTables.Contains(target)) fkToTarget[p.FkColumn] = target;
        }

        var cols = new List<SchemaColumnInfo>();
        foreach (var p in cls.Props)
        {
            if (EfSchemaParser.CollectionElement(p.RawType) is not null) continue;         // collection nav
            if (!EfSchemaParser.IsScalar(p.RawType) && knownTables.Contains(LastSegment(p.RawType)))
                continue;                                                                    // reference nav
            var isPk = p.IsKey || (composite?.Contains(p.Name) ?? false);
            var isFk = fkToTarget.TryGetValue(p.Name, out var refTable);
            cols.Add(new SchemaColumnInfo(
                p.Name, p.RawType, isPk, p.RawType.TrimEnd().EndsWith('?'), isFk, isFk ? refTable : null, null));
        }
        return cols;
    }

    private async Task<Dictionary<string, string>> FetchAllAsync(
        IReadOnlyList<string> paths, string branch, string repoId, string key, CancellationToken ct)
    {
        var result = new ConcurrentDictionary<string, string>();
        var sem = new SemaphoreSlim(FetchConcurrency);
        var done = 0;
        var tasks = paths.Distinct().Select(async p =>
        {
            await sem.WaitAsync(ct);
            try { result[p] = await _ado.GetFileContentAsync(p, branch, repoId, "branch", ct); }
            catch (Exception e) { _log.LogDebug(e, "fetch failed {Path}", p); }
            finally
            {
                sem.Release();
                var n = Interlocked.Increment(ref done);
                if (n % 100 == 0) Pub(key, EventKinds.System, $"  fetched {n} files…");
            }
        });
        await Task.WhenAll(tasks);
        return new Dictionary<string, string>(result);
    }

    private static string FirstSegment(string path)
    {
        var i = path.IndexOf('/');
        return i > 0 ? path[..i] : path;
    }

    private static string LastSegment(string type)
    {
        var t = type.Trim().TrimEnd('?');
        var i = t.LastIndexOf('.');
        return i >= 0 ? t[(i + 1)..] : t;
    }

    // ── Phase A classification ──────────────────────────────────────────────────

    /// <summary>Path-based file classification. Returns a bucket name, or null for "other" (counted only).
    /// dbContexts/entityConfigs/migrations/sql/entities feed Phase-B parsing; controllers/frontendApi feed
    /// the Phase-D flow overlay.</summary>
    public static string? Classify(string path)
    {
        var p = path.ToLowerInvariant();
        if (p.EndsWith(".sql")) return "sql";
        if (p.EndsWith(".cs") && p.Contains("/migrations/")) return "migrations";
        if (p.EndsWith("dbcontext.cs")) return "dbContexts";
        if (p.EndsWith("configuration.cs") && p.Contains("/configurations/")) return "entityConfigs";
        if (p.EndsWith("controller.cs")) return "controllers";
        if (p.EndsWith(".cs") && (p.Contains("/entities/") || p.Contains("/models/")
            || p.Contains("/domain/") || p.Contains("/entity/"))) return "entities";
        if ((p.EndsWith(".ts") || p.EndsWith(".tsx")) && (p.Contains("/api/") || p.Contains("/services/")))
            return "frontendApi";
        return null;
    }

    private async Task FailAsync(SchemaScan scan, string key, string message, CancellationToken ct)
    {
        Pub(key, EventKinds.Error, message, level: EventLevels.Error, terminal: true);
        try
        {
            scan.Status = SchemaScanStatus.Failed;
            scan.ErrorMessage = message;
            scan.Phase = null;
            scan.CompletedAt = DateTime.UtcNow;
            scan.EventLogJson = SerializeNarrative(key);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception e) { _log.LogError(e, "Failed to persist failure for scan {ScanId}", scan.Id); }
    }

    private string SerializeNarrative(string key) => JsonSerializer.Serialize(_bus.Narrative(key));

    private void Pub(string key, string kind, string? text = null, string? level = null,
        bool terminal = false, Dictionary<string, object?>? data = null)
    {
        if (terminal) { data ??= []; data["terminal"] = true; }
        _bus.Publish(key, new ConsoleEvent { Kind = kind, Text = text ?? "", Level = level ?? EventLevels.Info, Data = data });
    }
}
