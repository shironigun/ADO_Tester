using Microsoft.AspNetCore.Mvc;
using QaAutomation.Core.Abstractions;

namespace QaAutomation.Api.Controllers;

[ApiController]
[Route("api/knowledgebase")]
public class KnowledgebaseController : ControllerBase
{
    private readonly IKnowledgebaseService _kb;

    public KnowledgebaseController(IKnowledgebaseService kb) => _kb = kb;

    /// <summary>GET /api/knowledgebase/{projectId} — list uploaded docs.</summary>
    [HttpGet("{projectId:int}")]
    public async Task<IActionResult> List(int projectId, CancellationToken ct)
        => Ok(await _kb.ListAsync(projectId, ct));

    /// <summary>POST /api/knowledgebase/{projectId} — upload one or more .md files (multipart).</summary>
    [HttpPost("{projectId:int}")]
    [RequestSizeLimit(50_000_000)]
    public async Task<IActionResult> Upload(int projectId, [FromForm] List<IFormFile> files, CancellationToken ct)
    {
        if (files is null || files.Count == 0) return BadRequest("No files uploaded.");

        var results = new List<object>();
        foreach (var file in files)
        {
            if (!file.FileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                return BadRequest($"Only .md files are accepted ('{file.FileName}').");

            using var reader = new StreamReader(file.OpenReadStream());
            var markdown = await reader.ReadToEndAsync(ct);
            var doc = await _kb.UploadAsync(projectId, file.FileName, markdown, ct);
            results.Add(doc);
        }
        return Ok(results);
    }

    /// <summary>DELETE /api/knowledgebase/doc/{documentId}.</summary>
    [HttpDelete("doc/{documentId:int}")]
    public async Task<IActionResult> Delete(int documentId, CancellationToken ct)
    {
        await _kb.DeleteAsync(documentId, ct);
        return NoContent();
    }

    /// <summary>GET /api/knowledgebase/{projectId}/search?q=...&amp;k=8 — lexical retrieval test.</summary>
    [HttpGet("{projectId:int}/search")]
    public async Task<IActionResult> Search(int projectId, [FromQuery] string q, [FromQuery] int k = 8, CancellationToken ct = default)
        => Ok(await _kb.SearchAsync(projectId, q, k, ct));
}
