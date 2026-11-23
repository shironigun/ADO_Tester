namespace QaAutomation.Core.Models;

// ── DB Schema Explorer ──────────────────────────────────────────────────────────
// Product-agnostic database mindmap + live entity-trace module. A one-time read-only
// scan of a product's ADO repo builds the schema graph (services → tables → relations);
// the Entity Registry names the user-meaningful roots (Customer, Deal, Conversation, …);
// live SELECT-only queries then trace a real record across every linked table.
// Everything here is READ-ONLY with respect to the scanned product — we never write to
// its repo, and we never run anything but SELECT against its database.

/// <summary>A configured scan target — one product whose database we explore. Table: SchemaProducts.</summary>
public sealed class SchemaProduct
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>ADO project the source repo lives in (e.g. "My Project").</summary>
    public string AdoProject { get; set; } = string.Empty;
    /// <summary>ADO Git repository to scan (e.g. "MyRepo"). Multi-repo products come later.</summary>
    public string AdoRepository { get; set; } = string.Empty;
    /// <summary>Branch scanned by default (e.g. "master"/"main").</summary>
    public string DefaultBranch { get; set; } = "main";
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Lifecycle of a schema scan.</summary>
public enum SchemaScanStatus { Queued, Running, Succeeded, Failed, Cancelled }

/// <summary>One run of the read-only repo scan that builds a product's schema graph. Table: SchemaScans.</summary>
public sealed class SchemaScan
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public SchemaScanStatus Status { get; set; } = SchemaScanStatus.Queued;
    /// <summary>Human-readable current phase (Inventory | Parse | Infer | Flow), for live progress.</summary>
    public string? Phase { get; set; }
    /// <summary>Git commit the scan was taken at — the freshness marker used to offer a rescan.</summary>
    public string? CommitId { get; set; }
    public string? Branch { get; set; }
    public int ServiceCount { get; set; }
    public int TableCount { get; set; }
    public int RelationCount { get; set; }
    public int EntityCount { get; set; }
    /// <summary>Serialized extra stats / file-index summary from the inventory phase.</summary>
    public string? StatsJson { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>Serialized console narrative captured at completion so the log replays after SSE closes.</summary>
    public string? EventLogJson { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>A microservice / DbContext group — one "section" in the ERD. Table: SchemaServices.</summary>
public sealed class SchemaService
{
    public int Id { get; set; }
    public int ScanId { get; set; }
    /// <summary>Service/module name (e.g. "customer", "messaging").</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>The DbContext class this section was extracted from, if any.</summary>
    public string? DbContextClass { get; set; }
    /// <summary>Repo-relative folder the service lives under.</summary>
    public string? SourcePath { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>
/// A table (or keyless result shape) in the schema graph. Table: SchemaTables.
/// Columns are stored as a serialized <see cref="SchemaColumnInfo"/> list in <see cref="ColumnsJson"/>
/// rather than their own rows — the ERD popup reads them as a blob, never queries them individually.
/// </summary>
public sealed class SchemaTable
{
    public int Id { get; set; }
    public int ScanId { get; set; }
    /// <summary>Owning service; null when the owner is unknown or the table is an external reference.</summary>
    public int? ServiceId { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>SQL schema the table lives in (e.g. "dbo", "customer") — the colored chip in the ERD.</summary>
    public string? SqlSchema { get; set; }
    /// <summary>Keyless entity: a stored-procedure / view result shape, not a physical table.</summary>
    public bool IsKeyless { get; set; }
    /// <summary>Dashed node: this table is owned by another service and only referenced here.</summary>
    public bool IsExternalRef { get; set; }
    /// <summary>For an external reference, the name of the owning service.</summary>
    public string? OwnerServiceName { get; set; }
    /// <summary>Where the table was defined (file:line), for provenance.</summary>
    public string? SourceFile { get; set; }
    /// <summary>Serialized List&lt;SchemaColumnInfo&gt; — the full column list shown in the schema popup.</summary>
    public string? ColumnsJson { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>One column, serialized into <see cref="SchemaTable.ColumnsJson"/> (not a table).</summary>
public sealed record SchemaColumnInfo(
    string Name,
    string? DataType,
    bool IsPrimaryKey,
    bool IsNullable,
    bool IsForeignKey,
    string? ReferencesTable,
    string? ReferencesColumn);

/// <summary>How a relation was discovered.</summary>
public enum SchemaRelationKind
{
    /// <summary>An explicit FK constraint parsed from EF config / migrations. Highest confidence.</summary>
    ForeignKey,
    /// <summary>An EF navigation property with no DB-level FK constraint.</summary>
    EfNavigation,
    /// <summary>An implicit ID reference (dashed cross-service edge) recovered by the AI pass.</summary>
    Inferred,
}

/// <summary>An edge between two tables. Direction is source → target (source holds the reference).
/// Table: SchemaRelations. Table ids are loose indexed refs (scan-scoped), not FK constraints.</summary>
public sealed class SchemaRelation
{
    public int Id { get; set; }
    public int ScanId { get; set; }
    public int SourceTableId { get; set; }
    public int TargetTableId { get; set; }
    public string? SourceColumn { get; set; }
    public string? TargetColumn { get; set; }
    public SchemaRelationKind Kind { get; set; } = SchemaRelationKind.ForeignKey;
    /// <summary>0..1 confidence for inferred edges; null for hard FK/navigation edges.</summary>
    public double? Confidence { get; set; }
    /// <summary>Evidence for an inferred edge (file:line or a short quote), shown in the UI tooltip.</summary>
    public string? Evidence { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// A user-meaningful entity root in the registry (Customer, Deal, Conversation, …), or a
/// tenant-scoped settings entity (Oversending Control, Pipeline, Sync Settings) addressed by a
/// scope column instead of its own id. Tied to the PRODUCT (not a scan) and references its root
/// table by NAME so curation survives a rescan. Table: SchemaEntities.
/// </summary>
public sealed class SchemaEntity
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    /// <summary>Stable key (e.g. "customer", "deal", "oversending-control").</summary>
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>Root table this entity starts from, referenced by name so it survives rescans.</summary>
    public string? RootTableName { get; set; }
    /// <summary>The id column the user supplies (e.g. "CrmCustomerId", "DealId").</summary>
    public string? IdColumn { get; set; }
    /// <summary>For tenant-scoped settings entities: the scope column (e.g. "TenantId", "AccountId").</summary>
    public string? ScopeColumn { get; set; }
    /// <summary>Serialized list of columns used to build a human label (e.g. FirstName, LastName).</summary>
    public string? DisplayColumnsJson { get; set; }
    public double? Confidence { get; set; }
    /// <summary>"ai" (proposed by the scan) or "user" (curated). AI proposals become user once edited.</summary>
    public string Source { get; set; } = "ai";
    public bool IsEnabled { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A read-only DB connection used by the live explorer. Per product, optionally per service (a
/// microservice may own a separate database); ServiceName null = the product default. Table:
/// SchemaConnections. Only ever used to run SELECT statements — configure a read-only login.
/// </summary>
public sealed class SchemaConnection
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    /// <summary>Which environment this connection points at (e.g. "Dev", "QA", "Staging", "Production").
    /// The user picks the environment to trace against; a product can have several.</summary>
    public string Environment { get; set; } = "Default";
    /// <summary>Service this connection serves; null = the whole product (single-DB / multi-schema case).</summary>
    public string? ServiceName { get; set; }
    // Discrete fields entered in Settings → Database (the password lives only inside ConnectionString).
    public string? Server { get; set; }
    public string? DbUser { get; set; }
    public string? DbDatabase { get; set; }
    /// <summary>The effective SQL Server connection string, built from the discrete fields on save.</summary>
    public string ConnectionString { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
