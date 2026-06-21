using Microsoft.AspNetCore.Mvc;
using QaAutomation.Api.Streaming;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Contracts;
using QaAutomation.Core.Models;

namespace QaAutomation.Api.Controllers;

/// <summary>
/// Reports endpoints (spec B7/B9) — flat under <c>/api/reports</c>. SSE (key <c>report:{id}</c>),
/// preview (mock deck), generate (range-validated), list, detail, inline HTML, and forced download.
/// {detail} errors. <c>/preview</c> is declared before <c>/{projectId}</c> so it isn't parsed as an id.
/// </summary>
[ApiController]
[Route("api/reports")]
public sealed class ReportsController : ControllerBase
{
    private const int MaxRangeDays = 365;

    private readonly IReportService _reports;
    private readonly IReportHtmlBuilder _htmlBuilder;
    private readonly IAppSettingsService _settings;
    private readonly IEventBus _bus;
    private readonly ILogger<ReportsController> _log;

    public ReportsController(
        IReportService reports, IReportHtmlBuilder htmlBuilder, IAppSettingsService settings, IEventBus bus,
        ILogger<ReportsController> log)
    {
        _reports = reports;
        _htmlBuilder = htmlBuilder;
        _settings = settings;
        _bus = bus;
        _log = log;
    }

    [HttpGet("{reportId:int}/stream")]
    public async Task Stream(int reportId, [FromQuery] string? lastEventId, CancellationToken cancellationToken)
    {
        var afterSeq = SseWriter.ParseLastEventId(Request.Headers["Last-Event-ID"].FirstOrDefault(), lastEventId);
        await SseWriter.StreamAsync(Response, _bus, $"report:{reportId}", afterSeq, cancellationToken);
    }

    [HttpGet("preview")]
    public async Task<IActionResult> Preview([FromQuery] string theme = "Executive", [FromQuery] string? team = null)
    {
        var chosen = Enum.TryParse<ReportTheme>(theme, ignoreCase: true, out var t) ? t : ReportTheme.Executive;
        var teamName = team ?? await _settings.GetAsync("reports.team_name");
        return Content(_htmlBuilder.BuildMock(chosen, teamName), "text/html; charset=utf-8");
    }

    [HttpPost("{projectId:int}/generate")]
    public async Task<IActionResult> Generate(int projectId, [FromBody] GenerateReportRequest request, CancellationToken cancellationToken)
    {
        if (request.FromUtc >= request.ToUtc)
            return StatusCode(400, new { detail = "FromUtc must be before ToUtc." });
        var rangeDays = (request.ToUtc - request.FromUtc).TotalDays;
        if (rangeDays > MaxRangeDays)
            return StatusCode(400, new { detail = $"Date range {rangeDays:F0} days exceeds max {MaxRangeDays} days." });
        try
        {
            var reportId = await _reports.GenerateAsync(
                projectId, request.FromUtc, request.ToUtc, request.Boards ?? [], "Manual", request.Theme, cancellationToken);
            return Ok(new GenerateReportResponse { ReportId = reportId });
        }
        catch (Exception e)
        {
            _log.LogError(e, "Report generation kickoff failed for project {ProjectId}", projectId);
            return StatusCode(500, new { detail = e.Message });
        }
    }

    [HttpGet("{projectId:int}")]
    public async Task<IActionResult> List(int projectId, CancellationToken cancellationToken)
    {
        try
        {
            var rows = await _reports.ListAsync(projectId, cancellationToken);
            return Ok(rows.Select(ReportDto.From).ToList());
        }
        catch (Exception e)
        {
            return StatusCode(500, new { detail = e.Message });
        }
    }

    [HttpGet("detail/{reportId:int}")]
    public async Task<IActionResult> Detail(int reportId, CancellationToken cancellationToken)
    {
        try
        {
            var report = await _reports.GetAsync(reportId, cancellationToken);
            return report is null
                ? StatusCode(404, new { detail = "Report not found." })
                : Ok(ReportDto.From(report));
        }
        catch (Exception e)
        {
            return StatusCode(500, new { detail = e.Message });
        }
    }

    [HttpGet("html/{reportId:int}")]
    public async Task<IActionResult> Html(int reportId, CancellationToken cancellationToken)
    {
        try
        {
            var body = await _reports.ReadHtmlAsync(reportId, cancellationToken);
            return body is null
                ? StatusCode(404, new { detail = "Report HTML not found." })
                : Content(body, "text/html; charset=utf-8");
        }
        catch (Exception e)
        {
            return StatusCode(500, new { detail = e.Message });
        }
    }

    [HttpGet("download/{reportId:int}")]
    public async Task<IActionResult> Download(int reportId, CancellationToken cancellationToken)
    {
        try
        {
            var body = await _reports.ReadHtmlAsync(reportId, cancellationToken);
            if (body is null)
                return StatusCode(404, new { detail = "Report HTML not found." });
            var report = await _reports.GetAsync(reportId, cancellationToken);
            var name = report is not null
                ? $"report-{reportId}-{report.FromUtc:yyyyMMdd}-to-{report.ToUtc:yyyyMMdd}.html"
                : $"report-{reportId}.html";
            return File(System.Text.Encoding.UTF8.GetBytes(body), "text/html", name);
        }
        catch (Exception e)
        {
            return StatusCode(500, new { detail = e.Message });
        }
    }
}
