using Microsoft.Extensions.DependencyInjection;
using QaAutomation.Api.Configurations;
using QaAutomation.Api.Middlewares;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Infrastructure;

// Load local secrets (ADOACCESS_PAT, ANTHROPIC_API_KEY, …) from a .env file into process env vars.
// TraversePath searches up the directory tree so it works from the repo root or the project folder.
// NoClobber: existing env vars (set in the shell / Docker / EF tools) take precedence over .env.
DotNetEnv.Env.TraversePath().NoClobber().Load();

var builder = WebApplication.CreateBuilder(args);

// Single data root: <backend>/data — KB uploads, report HTML, Playwright runtime (not the app DB).
// For containerized/hosted deploys set Storage__DataDirectory to an absolute path (e.g. /app/data).
var configuredDataDir = builder.Configuration["Storage:DataDirectory"];
var dataDir = string.IsNullOrWhiteSpace(configuredDataDir)
    ? Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "data"))
    : Path.GetFullPath(configuredDataDir, builder.Environment.ContentRootPath);
builder.Configuration["Storage:DataDirectory"] = dataDir;

builder.Services.AddCustomControllers();
builder.Services.AddCustomCors();
var authOptions = AuthOptions.FromEnvironment();
// Outside Development the server binds non-loopback (Docker publishes :5250/:8080) and holds a live
// ADO PAT — refuse to run wide open.
if (!builder.Environment.IsDevelopment() && !authOptions.Enabled)
    throw new InvalidOperationException(
        "Login required outside Development: set APP_LOGIN_USERNAME and APP_LOGIN_PASSWORD in .env.");
// The random per-process fallback key logs everyone out on restart — fine for dev, not for a deploy.
if (!builder.Environment.IsDevelopment() && authOptions.Enabled && authOptions.JwtSecret.Length == 0)
    throw new InvalidOperationException(
        "Set APP_LOGIN_JWT_SECRET to a random value outside Development (the token signing key).");
builder.Services.AddCustomAuth(authOptions);
builder.Services.AddCustomSwagger();
builder.Services.AddProblemDetails();

// PostgreSQL only. Prefer POSTGRES_CONNECTION_STRING; DATABASE_URL accepted as alias.
var dbConnectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
    ?? Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? throw new InvalidOperationException(
        "PostgreSQL is required. Set POSTGRES_CONNECTION_STRING (or DATABASE_URL) in the environment / .env.");
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
builder.Services.AddInfrastructure(builder.Configuration, dbConnectionString);

var app = builder.Build();

app.Services.InitializeDatabase();
await app.Services.GetRequiredService<IAppSettingsService>().ApplyAsync();

app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCustomCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.Run();
