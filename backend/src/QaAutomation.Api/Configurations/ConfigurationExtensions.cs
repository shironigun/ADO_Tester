using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using QaAutomation.Core.Configuration;

namespace QaAutomation.Api.Configurations;

/// <summary>
/// House-style configuration extensions. <c>Program.cs</c> reads as a list of
/// <c>AddCustom*</c>/<c>Configure*</c> one-liners (mirrors a typical ASP.NET Core Web API host). New
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

    /// <summary>
    /// JWT bearer login gate. On login the client receives a signed token; every request must carry it
    /// (Authorization: Bearer …). Because EventSource/iframe requests can't set that header, the token
    /// is also accepted from an <c>?access_token=</c> query param. When auth is enabled a global fallback
    /// policy protects every endpoint except those marked <c>[AllowAnonymous]</c> (the auth endpoints +
    /// <c>/api/health</c>). The <c>AuthOptions</c> instance is registered for <c>AuthController</c>.
    /// </summary>
    public static IServiceCollection AddCustomAuth(this IServiceCollection services, AuthOptions auth)
    {
        services.AddSingleton(auth);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = AuthOptions.Issuer,
                    ValidateAudience = true,
                    ValidAudience = AuthOptions.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(auth.GetSigningKeyBytes()),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(1),
                };
                // SSE/EventSource and iframe/download requests can't send an Authorization header —
                // accept the token from ?access_token= as well (apiUrl() adds it to every direct URL).
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        var fromQuery = ctx.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(fromQuery))
                            ctx.Token = fromQuery;
                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorization(options =>
        {
            if (auth.Enabled)
                options.FallbackPolicy = new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .Build();
        });
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
