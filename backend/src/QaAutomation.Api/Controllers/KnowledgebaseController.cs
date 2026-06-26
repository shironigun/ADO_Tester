using System.Text;
using Microsoft.AspNetCore.Mvc;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Contracts;

namespace QaAutomation.Api.Controllers;

/// <summary>
/// Knowledgebase endpoints (spec B6/B9) — flat under <c>/api/knowledgebase</c>. Upload is multipart
/// (form field <c>files</c>, .md only, 10 MB/file, UTF-8 decode with replacement). Errors are
/// <c>{ detail }</c> (FastAPI parity). The <c>KnowledgebaseDocument</c>/<c>KnowledgebaseHit</c> models
/// already serialize to the frozen camelCase shapes.
/// </summary>
[ApiController]
[Route("api/knowledgebase")]
public sealed class KnowledgebaseController : ControllerBase
{
    private const int MaxFileBytes = 10_000_000;   // 10 MB per file
    private const int MaxK = 50;

    private readonly IKnowledgebaseService _kb;
    private readonly IKbExtractionService _kbExtraction;
    private readonly ILogger<KnowledgebaseController> _log;

    public KnowledgebaseController(
        IKnowledgebaseService kb, IKbExtractionService kbExtraction, ILogger<KnowledgebaseController> log)
    {
        _kb = kb;
        _kbExtraction = kbExtraction;
        _log = log;
    }

    [HttpGet("{projectId:int}")]
    public async Task<IActionResult> List(int projectId, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _kb.ListAsync(projectId, cancellationToken));
        }
        catch (Exception e)
        {
            _log.LogError(e, "KB list failed");
            return StatusCode(500, new { detail = e.Message });
        }
    }

    [HttpPost("{projectId:int}")]
    public async Task<IActionResult> Upload(int projectId, [FromForm] List<IFormFile> files, CancellationToken cancellationToken)
    {
        if (files is null || files.Count == 0)
            return StatusCode(400, new { detail = "No files uploaded." });

        try
        {
            var results = new List<Core.Models.KnowledgebaseDocument>();
            foreach (var file in files)
            {
                var name = file.FileName ?? "";
                if (!name.ToLowerInvariant().EndsWith(".md", StringComparison.Ordinal))
                    return StatusCode(400, new { detail = $"Only .md files are accepted ('{name}')." });

                using var ms = new MemoryStream();
                await file.CopyToAsync(ms, cancellationToken);
                var raw = ms.ToArray();
                if (raw.Length > MaxFileBytes)
                    return StatusCode(400, new { detail = $"'{name}' is {raw.Length / 1024} KB — max per file is {MaxFileBytes / 1024} KB." });

                // UTF-8 with replacement (the static Encoding.UTF8 substitutes U+FFFD), matching Python errors='replace'.
                var markdown = Encoding.UTF8.GetString(raw);
                results.Add(await _kb.UploadAsync(projectId, name, markdown, cancellationToken));
            }
            return Ok(results);
        }
        catch (Exception e)
        {
            _log.LogError(e, "KB upload failed");
            return StatusCode(500, new { detail = e.Message });
        }
    }

    [HttpDelete("doc/{documentId:int}")]
    public async Task<IActionResult> Delete(int documentId, CancellationToken cancellationToken)
    {
        try
        {
            await _kb.DeleteAsync(documentId, cancellationToken);
            return NoContent();   // 204
        }
        catch (Exception e)
        {
            _log.LogError(e, "KB delete failed");
            return StatusCode(500, new { detail = e.Message });
        }
    }

    [HttpGet("{projectId:int}/search")]
    public async Task<IActionResult> Search(
        int projectId, [FromQuery] string? q, [FromQuery] int k = 8, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(q) || q.Trim().Length < 2)
            return StatusCode(400, new { detail = "Query must be at least 2 characters." });
        k = Math.Max(1, Math.Min(k, MaxK));
        try
        {
            return Ok(await _kb.SearchAsync(projectId, q, k, cancellationToken));
        }
        catch (Exception e)
        {
            _log.LogError(e, "KB search failed");
            return StatusCode(500, new { detail = e.Message });
        }
    }

    /// <summary>Extract a module's knowledgebase from product source (Track A): reads the module's code +
    /// existing tests from <c>SourceRoot</c>, runs the KB-extractor agent once per flow (+ rules.md), and
    /// stores + indexes each produced doc. Long-running (one LLM call per flow); awaited.</summary>
    [HttpPost("{projectId:int}/extract")]
    public async Task<IActionResult> Extract(
        int projectId, [FromBody] KbExtractionRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
            return StatusCode(400, new { detail = "Request body is required." });
        try
        {
            return Ok(await _kbExtraction.ExtractAsync(projectId, request, cancellationToken));
        }
        catch (ArgumentException e)
        {
            return StatusCode(400, new { detail = e.Message });
        }
        catch (DirectoryNotFoundException e)
        {
            return StatusCode(400, new { detail = e.Message });
        }
        catch (Exception e)
        {
            _log.LogError(e, "KB extraction failed");
            return StatusCode(500, new { detail = e.Message });
        }
    }
}
