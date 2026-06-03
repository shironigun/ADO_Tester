using QaAutomation.Infrastructure;

// Load secrets (ADOACCESS_PAT, ANTHROPIC_API_KEY) from a .env file into process env vars.
// Searches up the directory tree so it works whether run from repo root or the project folder.
DotNetEnv.Env.TraversePath().Load();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters
        .Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddInfrastructure(builder.Configuration);

// Allow the Vite dev server (port 5173) during local development
builder.Services.AddCors(opts =>
    opts.AddPolicy("DevCors", p =>
        p.WithOrigins("http://localhost:5173")
         .AllowAnyHeader()
         .AllowAnyMethod()));

var app = builder.Build();

app.Services.InitializeInfrastructure();

app.UseCors("DevCors");
app.UseAuthorization();
app.MapControllers();

app.Run();
