using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QaAutomation.Core.Abstractions;

namespace QaAutomation.Infrastructure.Reports;

/// <summary>
/// Weekly report cron (spec B7) — Thursday 11:00 in <c>Asia/Karachi</c>. Like the Python, the app must
/// be running at the trigger moment; there is NO catch-up for a missed slot. Honors the live settings
/// toggle + default boards/range. Computes the next slot explicitly (Karachi has no DST).
/// </summary>
public sealed class WeeklyReportScheduler : BackgroundService
{
    private const int DefaultProjectId = 1;

    private readonly IReportService _reports;
    private readonly IAppSettingsService _settings;
    private readonly ILogger<WeeklyReportScheduler> _log;
    private readonly TimeZoneInfo _tz;

    public WeeklyReportScheduler(IReportService reports, IAppSettingsService settings, ILogger<WeeklyReportScheduler> log)
    {
        _reports = reports;
        _settings = settings;
        _log = log;
        _tz = ResolveKarachi();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("WeeklyReportScheduler started (timezone={Tz})", _tz.Id);
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeUntilNextThursday11();
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }
            if (stoppingToken.IsCancellationRequested)
                break;

            await FireAsync();
            // Step past the trigger minute so the next recompute lands on next week's slot.
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private TimeSpan TimeUntilNextThursday11()
    {
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _tz);
        var daysUntilThu = (((int)DayOfWeek.Thursday - (int)nowLocal.DayOfWeek) + 7) % 7;
        var next = nowLocal.Date.AddDays(daysUntilThu).AddHours(11);
        if (next <= nowLocal)
            next = next.AddDays(7);
        return next - nowLocal;
    }

    private async Task FireAsync()
    {
        try
        {
            var enabled = (await _settings.GetAsync("reports.schedule_enabled")).ToLowerInvariant();
            if (enabled is not ("true" or "1" or "yes" or "on"))
            {
                _log.LogInformation("Weekly report skipped — schedule disabled in settings");
                return;
            }

            var days = int.TryParse(await _settings.GetAsync("reports.default_range_days"), out var d) && d > 0 ? d : 7;
            var boardsCsv = await _settings.GetAsync("reports.default_boards");
            var boards = boardsCsv.Replace(";", ",").Split(',').Select(b => b.Trim()).Where(b => b.Length > 0).ToList();

            var toUtc = DateTime.UtcNow;
            var fromUtc = toUtc.AddDays(-days);
            _log.LogInformation("Weekly report fire: {From} → {To} (boards={Boards})", fromUtc, toUtc, string.Join(",", boards));
            await _reports.GenerateAsync(DefaultProjectId, fromUtc, toUtc, boards, trigger: "Scheduled");
        }
        catch (Exception e)
        {
            _log.LogError(e, "Weekly report fire failed");
        }
    }

    private static TimeZoneInfo ResolveKarachi()
    {
        foreach (var id in new[] { "Asia/Karachi", "Pakistan Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Utc;   // last-resort fallback
    }
}
