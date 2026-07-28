using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace QaAutomation.Infrastructure.Data;

/// <summary>
/// Design-time factory used only by the EF Core tools (<c>dotnet ef migrations</c>). The connection
/// string is a throwaway — migrations are generated from the model, not from a real database.
/// At runtime the context is created through DI (<see cref="DependencyInjection.AddInfrastructure"/>).
/// </summary>
public sealed class QaDbContextFactory : IDesignTimeDbContextFactory<QaDbContext>
{
    public QaDbContext CreateDbContext(string[] args)
    {
        // Branch on DATABASE_PROVIDER so `dotnet ef` can scaffold/apply migrations for either provider.
        // (This factory runs INSTEAD of Program.cs, so DotNetEnv is not loaded — read the shell env.
        // For `migrations add` the connection string is never opened, so the Postgres fallback is fine;
        // for `database update` export a real POSTGRES_CONNECTION_STRING.)
        var provider = (Environment.GetEnvironmentVariable("DATABASE_PROVIDER") ?? "sqlite")
            .Trim().ToLowerInvariant();
        var builder = new DbContextOptionsBuilder<QaDbContext>();
        if (provider is "postgres" or "postgresql" or "npgsql")
        {
            var cs = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
                ?? Environment.GetEnvironmentVariable("DATABASE_URL")
                ?? "Host=localhost;Port=5432;Database=qa_designtime;Username=postgres;Password=postgres";
            builder.UseNpgsql(cs, n => n.MigrationsAssembly("QaAutomation.Migrations.Npgsql"));
        }
        else
        {
            builder.UseSqlite("Data Source=qa_designtime.db");
        }
        return new QaDbContext(builder.Options);
    }
}
