namespace QaAutomation.Core.Models;

/// <summary>Metadata for an uploaded .md domain-base file. Content is chunked into the FTS index.</summary>
public sealed class KnowledgebaseDocument
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string StoredPath { get; set; } = string.Empty;
    public int ChunkCount { get; set; }
    public long SizeBytes { get; set; }
    public DateTime UploadedAt { get; set; }
}

/// <summary>A retrieved chunk with its relevance score (lower bm25 = better).</summary>
public sealed record KnowledgebaseHit(int DocumentId, string FileName, string Content, double Score);
