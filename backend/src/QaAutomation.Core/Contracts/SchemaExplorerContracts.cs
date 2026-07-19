using QaAutomation.Core.Models;

namespace QaAutomation.Core.Contracts;

// ── DB Schema Explorer — /api/schema ─────────────────────────────────────────────

public sealed class SchemaProductDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string AdoProject { get; set; } = "";
    public string AdoRepository { get; set; } = "";
    public string DefaultBranch { get; set; } = "";
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    /// <summary>Summary of the most recent scan, if any (drives the "last scanned" chip).</summary>
    public SchemaScanDto? LatestScan { get; set; }

    public static SchemaProductDto From(SchemaProduct p, SchemaScan? latest = null) => new()
    {
        Id = p.Id, Name = p.Name, AdoProject = p.AdoProject, AdoRepository = p.AdoRepository,
        DefaultBranch = p.DefaultBranch, Description = p.Description,
        CreatedAt = p.CreatedAt, UpdatedAt = p.UpdatedAt,
        LatestScan = latest is null ? null : SchemaScanDto.From(latest),
    };
}

public sealed class CreateProductRequest
{
    public string Name { get; set; } = "";
    public string AdoProject { get; set; } = "";
    public string AdoRepository { get; set; } = "";
    public string DefaultBranch { get; set; } = "main";
    public string? Description { get; set; }
}

public sealed class UpdateProductRequest
{
    public string? Name { get; set; }
    public string? AdoProject { get; set; }
    public string? AdoRepository { get; set; }
    public string? DefaultBranch { get; set; }
    public string? Description { get; set; }
}

public sealed class SchemaProductList
{
    public int Total { get; set; }
    public IReadOnlyList<SchemaProductDto> Items { get; set; } = [];
}

public sealed class SchemaScanDto
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public string Status { get; set; } = "";
    public string? Phase { get; set; }
    public string? CommitId { get; set; }
    public string? Branch { get; set; }
    public int ServiceCount { get; set; }
    public int TableCount { get; set; }
    public int RelationCount { get; set; }
    public int EntityCount { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public static SchemaScanDto From(SchemaScan s) => new()
    {
        Id = s.Id, ProductId = s.ProductId, Status = s.Status.ToString(), Phase = s.Phase,
        CommitId = s.CommitId, Branch = s.Branch,
        ServiceCount = s.ServiceCount, TableCount = s.TableCount,
        RelationCount = s.RelationCount, EntityCount = s.EntityCount,
        ErrorMessage = s.ErrorMessage, StartedAt = s.StartedAt,
        CompletedAt = s.CompletedAt, CreatedAt = s.CreatedAt,
    };
}

public sealed class SchemaScanList
{
    public int Total { get; set; }
    public IReadOnlyList<SchemaScanDto> Items { get; set; } = [];
}

// ── Graph (Phase B output) ──────────────────────────────────────────────────────

public sealed class SchemaServiceDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? DbContextClass { get; set; }
    public string? SourcePath { get; set; }
    public int TableCount { get; set; }

    public static SchemaServiceDto From(SchemaService s, int tableCount) => new()
    {
        Id = s.Id, Name = s.Name, DbContextClass = s.DbContextClass,
        SourcePath = s.SourcePath, TableCount = tableCount,
    };
}

public sealed class SchemaTableDto
{
    public int Id { get; set; }
    public int? ServiceId { get; set; }
    public string Name { get; set; } = "";
    public string? SqlSchema { get; set; }
    public bool IsKeyless { get; set; }
    public IReadOnlyList<SchemaColumnInfo> Columns { get; set; } = [];
}

public sealed class SchemaRelationDto
{
    public int Id { get; set; }
    public int SourceTableId { get; set; }
    public int TargetTableId { get; set; }
    public string? SourceColumn { get; set; }
    public string Kind { get; set; } = "";
    public double? Confidence { get; set; }
    public string? Evidence { get; set; }

    public static SchemaRelationDto From(SchemaRelation r) => new()
    {
        Id = r.Id, SourceTableId = r.SourceTableId, TargetTableId = r.TargetTableId,
        SourceColumn = r.SourceColumn, Kind = r.Kind.ToString(),
        Confidence = r.Confidence, Evidence = r.Evidence,
    };
}

public sealed class SchemaGraphDto
{
    public int ScanId { get; set; }
    public IReadOnlyList<SchemaServiceDto> Services { get; set; } = [];
    public IReadOnlyList<SchemaTableDto> Tables { get; set; } = [];
    public IReadOnlyList<SchemaRelationDto> Relations { get; set; } = [];
}

// ── Entity registry (Phase C output) ─────────────────────────────────────────────

public sealed class SchemaEntityDto
{
    public int Id { get; set; }
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? RootTableName { get; set; }
    public string? IdColumn { get; set; }
    public string? ScopeColumn { get; set; }
    public IReadOnlyList<string> DisplayColumns { get; set; } = [];
    public double? Confidence { get; set; }
    public string Source { get; set; } = "ai";
    public bool IsEnabled { get; set; }

    public static SchemaEntityDto From(SchemaEntity e, IReadOnlyList<string> displayColumns) => new()
    {
        Id = e.Id, Key = e.Key, DisplayName = e.DisplayName, RootTableName = e.RootTableName,
        IdColumn = e.IdColumn, ScopeColumn = e.ScopeColumn, DisplayColumns = displayColumns,
        Confidence = e.Confidence, Source = e.Source, IsEnabled = e.IsEnabled,
    };
}

public sealed class SchemaEntityList
{
    public int Total { get; set; }
    public IReadOnlyList<SchemaEntityDto> Items { get; set; } = [];
}

// ── Live trace / connection ──────────────────────────────────────────────────────

public sealed class SetConnectionRequest
{
    /// <summary>Environment name this connection targets (e.g. "Dev", "QA", "Staging", "Production").</summary>
    public string Environment { get; set; } = "Default";
    public string Server { get; set; } = "";
    public string User { get; set; } = "";
    /// <summary>Read-only login password. Leave blank when editing to keep the existing one.</summary>
    public string? Password { get; set; }
    public string Database { get; set; } = "";
}

public sealed class TestConnectionRequest
{
    public string? Environment { get; set; }
    public string? Server { get; set; }
    public string? User { get; set; }
    public string? Password { get; set; }
    public string? Database { get; set; }
}

/// <summary>One configured environment for a product (the password is never returned).</summary>
public sealed class EnvironmentConnection
{
    public string Environment { get; set; } = "";
    public string? Server { get; set; }
    public string? User { get; set; }
    public string? Database { get; set; }
}

public sealed class ConnectionList
{
    public IReadOnlyList<EnvironmentConnection> Items { get; set; } = [];
}

public sealed class TraceRequest
{
    public string EntityKey { get; set; } = "";
    public string Id { get; set; } = "";
    /// <summary>Environment to query; if omitted, the default/most-recent environment is used.</summary>
    public string? Environment { get; set; }
}
