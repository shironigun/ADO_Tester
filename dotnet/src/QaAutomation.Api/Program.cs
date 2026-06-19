using QaAutomation.Api.Configurations;
using QaAutomation.Api.Middlewares;
using QaAutomation.Infrastructure;

// Load local secrets (ADOACCESS_PAT, ANTHROPIC_API_KEY, …) from a .env file into process env vars.
// TraversePath searches up the directory tree so it works from the repo root or the project folder.
DotNetEnv.Env.TraversePath().Load();

var builder = WebApplication.CreateBuilder(args);

// SQLite db path. Defaults to the live qa.db shared with the Python app during migration (D6 adopts it).
// Override via config key Storage:DatabasePath (env: Storage__DatabasePath). Relative paths resolve
// against the content root (the Api project dir under `dotnet run`).
var configuredDbPath = builder.Configuration["Storage:DatabasePath"];
var dbPath = string.IsNullOrWhiteSpace(configuredDbPath)
    ? Path.Combine("..", "..", "..", "backend", "data", "qa.db")
    : configuredDbPath;
if (!Path.IsPathRooted(dbPath))
    dbPath = Path.GetFullPath(dbPath, builder.Environment.ContentRootPath);

// Program.cs stays a list of AddCustom*/Configure* one-liners (house style — see ConfigurationExtensions).
builder.Services.AddCustomControllers();
builder.Services.AddCustomCors();
builder.Services.AddCustomSwagger();
builder.Services.AddProblemDetails();
builder.Services.AddInfrastructure(builder.Configuration, $"Data Source={dbPath}");

var app = builder.Build();

// Migrate/adopt the DB, bootstrap FTS, seed Project id=1 (spec §6 B1 startup ordering).
app.Services.InitializeDatabase();

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
