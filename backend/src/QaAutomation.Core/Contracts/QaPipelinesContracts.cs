using QaAutomation.Core.Models;

namespace QaAutomation.Core.Contracts;

// ── Pipelines / Watches ───────────────────────────────────────────────────────

public sealed class PipelineWatchDto
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public int AdoPipelineId { get; set; }
    public string AdoPipelineName { get; set; } = "";
    public string Module { get; set; } = "";
    public int? LastSeenBuildId { get; set; }
    public DateTime? LastPollAt { get; set; }
    public string? LastPollError { get; set; }
    public bool Enabled { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public static PipelineWatchDto From(PipelineWatch w) => new()
    {
        Id = w.Id, ProjectId = w.ProjectId, AdoPipelineId = w.AdoPipelineId,
        AdoPipelineName = w.AdoPipelineName, Module = w.Module,
        LastSeenBuildId = w.LastSeenBuildId, LastPollAt = w.LastPollAt,
        LastPollError = w.LastPollError, Enabled = w.Enabled,
        CreatedAt = w.CreatedAt, UpdatedAt = w.UpdatedAt,
    };
}

public sealed class AddWatchRequest
{
    public int AdoPipelineId { get; set; }
    public string AdoPipelineName { get; set; } = "";
    public string Module { get; set; } = "";
}

public sealed class UpdateWatchRequest
{
    public bool? Enabled { get; set; }
    public string? Module { get; set; }
}

public sealed class RunLatestResponse
{
    public int? RunId { get; set; }
    public int? BuildId { get; set; }
}

public sealed class PreviewImpactRequest
{
    public List<string> ChangedFiles { get; set; } = [];
    public List<string> CommitMessages { get; set; } = [];
}

public sealed class PipelineWatchList
{
    public int Total { get; set; }
    public IReadOnlyList<PipelineWatchDto> Items { get; set; } = [];
}
