namespace QaAutomation.Core.Models;

/// <summary>A configured ADO pipeline monitored by the watcher. Table: PipelineWatches.</summary>
public sealed class PipelineWatch
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public int AdoPipelineId { get; set; }
    public string AdoPipelineName { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public int? LastSeenBuildId { get; set; }
    public DateTime? LastPollAt { get; set; }
    public string? LastPollError { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
