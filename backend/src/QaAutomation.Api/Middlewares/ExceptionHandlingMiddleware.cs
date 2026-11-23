using System.Text.Json;

namespace QaAutomation.Api.Middlewares;

/// <summary>
/// Catches unhandled exceptions and writes an RFC 7807 ProblemDetails <c>500</c> (spec §5.1:
/// unhandled → 500). Registered FIRST in the pipeline (mirrors a typical ASP.NET Core Web API host) so it
/// wraps everything downstream.
/// <para>
/// Domain errors are mapped to their status codes in the controllers themselves (ADO failures → 502,
/// validation → 400, not-found → 404); whatever reaches here is unexpected, so it returns a generic
/// 500 with no detail (the message is logged, never sent to the client).
/// </para>
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception for {Method} {Path}",
                context.Request.Method, context.Request.Path);

            // Can't rewrite a response already on the wire (e.g. mid-SSE-stream) — let it abort.
            if (context.Response.HasStarted)
                throw;

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/problem+json";

            var problem = new
            {
                type = "https://tools.ietf.org/html/rfc9110#section-15.6.1",
                title = "An unexpected error occurred.",
                status = StatusCodes.Status500InternalServerError,
            };
            await context.Response.WriteAsync(JsonSerializer.Serialize(problem));
        }
    }
}
