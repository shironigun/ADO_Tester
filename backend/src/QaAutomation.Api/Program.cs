using Microsoft.Extensions.DependencyInjection;
using QaAutomation.Api.Configurations;
using QaAutomation.Api.Middlewares;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Infrastructure;

// Load local secrets (ADOACCESS_PAT, ANTHROPIC_API_KEY, …) from a .env file into process env vars.
// TraversePath searches up the directory tree so it works from the repo root or the project folder.
DotNetEnv.Env.TraversePath().Load();

var builder = WebApplication.CreateBuilder(args);

// Single data root: <backend>/data — the API project sits at <backend>/src/QaAutomation.Api, so
// ../../data resolves to the backend's own data folder (name-independent). Holds the SQLite DB, KB
// uploads, and report HTML, so everything lives under one folder. Override the DB via Storage:DatabasePath
// (env: Storage__DatabasePath); relative overrides resolve against the content root.
var dataDir = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "data"));
var configuredDbPath = builder.Configuration["Storage:DatabasePath"];
var dbPath = string.IsNullOrWhiteSpace(configuredDbPath)
    ? Path.Combine(dataDir, "qa.db")
    : (Path.IsPathRooted(configuredDbPath)
        ? configuredDbPath
        : Path.GetFullPath(configuredDbPath, builder.Environment.ContentRootPath));
// Point KB/reports at the same data root (they bind Storage:DataDirectory).
builder.Configuration["Storage:DataDirectory"] = dataDir;

// Program.cs stays a list of AddCustom*/Configure* one-liners (house style — see ConfigurationExtensions).
builder.Services.AddCustomControllers();
builder.Services.AddCustomCors();
builder.Services.AddCustomSwagger();
builder.Services.AddProblemDetails();

// Database provider (spec: PostgreSQL migration). Default = sqlite, so behavior is unchanged until
// DATABASE_PROVIDER=postgres is set. Bound once here at boot; switching providers means a restart.
var dbProviderName = (Environment.GetEnvironmentVariable("DATABASE_PROVIDER") ?? "sqlite").Trim().ToLowerInvariant();
DatabaseProvider dbProvider;
string dbConnectionString;
if (dbProviderName is "postgres" or "postgresql" or "npgsql")
{
    dbProvider = DatabaseProvider.Postgres;
    dbConnectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
        ?? Environment.GetEnvironmentVariable("DATABASE_URL")
        ?? throw new InvalidOperationException(
            "DATABASE_PROVIDER=postgres but neither POSTGRES_CONNECTION_STRING nor DATABASE_URL is set.");
    // The app writes DateTime.UtcNow throughout; legacy behavior maps DateTime to 'timestamp without
    // time zone', so no model changes are needed for the first Postgres port (see solution.md Data Design).
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
}
else
{
    dbProvider = DatabaseProvider.Sqlite;
    dbConnectionString = $"Data Source={dbPath}";
}
builder.Services.AddInfrastructure(builder.Configuration, dbProvider, dbConnectionString);

var app = builder.Build();

// Migrate/adopt the DB, bootstrap FTS, seed Project id=1 (spec §6 B1 startup ordering).
app.Services.InitializeDatabase();

// Pull UI-edited overrides (Settings table) into the live options before serving (spec B9 apply()).
await app.Services.GetRequiredService<IAppSettingsService>().ApplyAsync();

// Exception handling FIRST so it wraps every downstream component (spec §4).
app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCustomCors();
app.MapControllers();

// Liveness probe (spec §5.4): GET /api/health → { "status": "ok" }.
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.Run();
