using System.Text.Json.Serialization;
using Microsoft.OpenApi.Models;

namespace QaAutomation.Api.Configurations;

/// <summary>
/// House-style configuration extensions. <c>Program.cs</c> reads as a list of
/// <c>AddCustom*</c>/<c>Configure*</c> one-liners (mirrors <c>TargetCrm.WebAPI</c>). New
/// cross-cutting wiring is added here as later phases land (B9 introduces strongly-typed
/// options with <c>[Required]</c> + <c>ValidateDataAnnotations()</c>).
/// </summary>
public static class ConfigurationExtensions
{
    /// <summary>CORS policy name for the local Vite dev server(s).</summary>
    public const string DevCorsPolicy = "DevCors";

    /// <summary>Vite dev origins: 5173 default, 5174 fallback when 5173 is taken (spec §5.4).</summary>
    private static readonly string[] DevOrigins = { "http://localhost:5173", "http://localhost:5174" };

    public static IServiceCollection AddCustomControllers(this IServiceCollection services)
    {
        services.AddControllers()
            // Enums serialize as their EXACT name (NOT int, NOT camelCased) — frozen contract (spec §5.1/§5.2).
            // Property names default to camelCase via the ASP.NET Core web defaults.
            .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        return services;
    }

    public static IServiceCollection AddCustomCors(this IServiceCollection services)
    {
        services.AddCors(o => o.AddPolicy(DevCorsPolicy, p =>
            p.WithOrigins(DevOrigins).AllowAnyHeader().AllowAnyMethod()));
        return services;
    }

    /// <summary>Applies the dev CORS policy. Encapsulated so <c>Program.cs</c> need not name this
    /// type (avoids a clash with <c>Microsoft.Extensions.Configuration.ConfigurationExtensions</c>).</summary>
    public static IApplicationBuilder UseCustomCors(this IApplicationBuilder app)
        => app.UseCors(DevCorsPolicy);

    public static IServiceCollection AddCustomSwagger(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(c => c.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "QA Automation API",
            Version = "v1",
            Description = "ADO_Tester backend — Author Tests / Find Bugs / Reports (.NET port).",
        }));
        return services;
    }
}
