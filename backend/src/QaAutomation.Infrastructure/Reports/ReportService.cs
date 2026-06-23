using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Data;

namespace QaAutomation.Infrastructure.Reports;

/// <summary>
/// Report generation (spec B7). Seeds a <see cref="Report"/> row synchronously (caller gets an id),
/// then runs a background <see cref="Task"/> on its own scope: query ADO tickets in range → enrich →
/// summarize (AI) → build self-contained HTML → write under the reports dir → status Done/Failed.
/// Live progress streams to the EventBus (<c>report:{id}</c>) and the narrative persists to EventLogJson.
/// Singleton.
/// </summary>
public sealed class ReportService : IReportService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAzureDevOpsClient _ado;
    private readonly ReportSummarizer _summarizer;
    private readonly IReportHtmlBuilder _htmlBuilder;
    private readonly IEventBus _bus;
    private readonly IAppSettingsService _settings;
    private readonly ILogger<ReportService> _log;
    private readonly string _reportsDir;

    public ReportService(
        IServiceScopeFactory scopeFactory, IAzureDevOpsClient ado, ReportSummarizer summarizer,
        IReportHtmlBuilder htmlBuilder, IEventBus bus, IAppSettingsService settings,
        IOptions<StorageOptions> storage, ILogger<ReportService> log)
    {
        _scopeFactory = scopeFactory;
        _ado = ado;
        _summarizer = summarizer;
        _htmlBuilder = htmlBuilder;
        _bus = bus;
        _settings = settings;
        _log = log;
        var opts = storage.Value;
        _reportsDir = Path.GetFullPath(Path.Combine(opts.DataDirectory, opts.ReportsFolderName));
    }

    public async Task<int> GenerateAsync(
        int projectId, DateTime fromUtc, DateTime toUtc, IReadOnlyList<string> boards,
        string trigger = "Manual", ReportTheme theme = ReportTheme.Executive, CancellationToken cancellationToken = default)
    {
        int reportId;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var report = new Report
            {
                ProjectId = projectId,
                FromUtc = fromUtc,
                ToUtc = toUtc,
                Boards = string.Join("; ", boards),
                Status = ReportStatus.Generating,
                CreatedAt = DateTime.UtcNow,
                Title = $"Weekly Report — {fromUtc:yyyy-MM-dd} to {toUtc:yyyy-MM-dd}",
                Trigger = trigger,
                Theme = theme,
            };
            db.Reports.Add(report);
            await db.SaveChangesAsync(cancellationToken);
            reportId = report.Id;
        }

        _ = Task.Run(() => GenerateInBackgroundAsync(reportId, fromUtc, toUtc, boards, theme));
        return reportId;
    }

    public async Task<IReadOnlyList<Report>> ListAsync(int projectId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        return await db.Reports.Where(r => r.ProjectId == projectId).OrderByDescending(r => r.CreatedAt).ToListAsync(cancellationToken);
    }

    public async Task<Report?> GetAsync(int reportId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        return await db.Reports.AsNoTracking().FirstOrDefaultAsync(r => r.Id == reportId, cancellationToken);
    }

    public async Task<string?> ReadHtmlAsync(int reportId, CancellationToken cancellationToken = default)
    {
        var report = await GetAsync(reportId, cancellationToken);
        if (report is null || string.IsNullOrEmpty(report.HtmlPath) || !File.Exists(report.HtmlPath))
            return null;
        return await File.ReadAllTextAsync(report.HtmlPath, cancellationToken);
    }

    private async Task GenerateInBackgroundAsync(int reportId, DateTime fromUtc, DateTime toUtc, IReadOnlyList<string> boards, ReportTheme theme)
    {
        var key = $"report:{reportId}";
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var report = await db.Reports.FindAsync(reportId);
        if (report is null)
            return;

        try
        {
            Pub(key, EventKinds.System, text: "Report generation started");

            var typesRaw = await _settings.GetAsync("reports.ticket_types");
            var ticketTypes = typesRaw.Split(';').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
            var tickets = await _ado.GetTicketsInRangeAsync(fromUtc, toUtc, boards, ticketTypes);
            var boardsLabel = boards.Count == 0 ? "" : "Boards: " + string.Join(", ", boards);
            Pub(key, EventKinds.Result, EventPhases.Fetch, $"Pulled {tickets.Count} ticket(s) in range", EventLevels.Success, new() { ["tickets"] = tickets.Count });

            Pub(key, EventKinds.Phase, EventPhases.Enrich, "Loading ticket history & comments…");
            tickets = await _ado.EnrichTicketsForReportAsync(tickets);
            Pub(key, EventKinds.Result, EventPhases.Enrich, "Ticket history & comments loaded", EventLevels.Success);

            Pub(key, EventKinds.Phase, EventPhases.Summarize, "Summarizer — generating AI insights");
            var summary = await _summarizer.SummarizeAsync(tickets, EmitClosure(key, EventPhases.Summarize));
            report.SummaryJson = summary.ToJson();
            await db.SaveChangesAsync();
            Pub(key, EventKinds.Result, EventPhases.Summarize, "AI insights generated", EventLevels.Success);

            Pub(key, EventKinds.System, EventPhases.Summarize, "Rendering self-contained HTML…");
            var teamName = await _settings.GetAsync("reports.team_name");
            var html = _htmlBuilder.Build(report.Title, fromUtc, toUtc, boardsLabel, tickets, theme, summary, teamName, boards);

            Directory.CreateDirectory(_reportsDir);
            var fileName = $"report-{reportId}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..8]}.html";
            var path = Path.Combine(_reportsDir, fileName);
            await File.WriteAllTextAsync(path, html);

            report.TicketCount = tickets.Count;
            report.HtmlPath = path;
            report.Status = ReportStatus.Done;
            Pub(key, EventKinds.System, EventPhases.Summarize, $"Report ready · {tickets.Count} ticket(s)", EventLevels.Success, new() { ["terminal"] = true });
            PersistLog(report, key);
            await db.SaveChangesAsync();
        }
        catch (Exception e)
        {
            _log.LogError(e, "Report {ReportId} generation failed", reportId);
            report.Status = ReportStatus.Failed;
            report.ErrorMessage = e.Message;
            Pub(key, EventKinds.Error, text: $"Failed: {e.Message}", level: EventLevels.Error, data: new() { ["terminal"] = true });
            PersistLog(report, key);
            try { await db.SaveChangesAsync(); } catch { /* best effort */ }
        }
    }

    private Action<LlmEmit> EmitClosure(string key, string phase)
        => emit => _bus.Publish(key, new ConsoleEvent { Kind = emit.Kind, Text = emit.Text, Phase = phase });

    private void Pub(string key, string kind, string? phase = null, string text = "", string level = EventLevels.Info, Dictionary<string, object?>? data = null)
        => _bus.Publish(key, new ConsoleEvent { Kind = kind, Phase = phase, Text = text, Level = level, Data = data });

    private void PersistLog(Report report, string key)
    {
        try { report.EventLogJson = JsonSerializer.Serialize(_bus.Narrative(key)); }
        catch { /* never block on log persistence */ }
    }
}
