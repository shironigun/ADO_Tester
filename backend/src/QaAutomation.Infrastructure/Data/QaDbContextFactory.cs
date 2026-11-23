using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace QaAutomation.Infrastructure.Data;

/// <summary>
/// Design-time factory for <c>dotnet ef migrations</c>. Always targets PostgreSQL
/// (<see cref="QaAutomation.Migrations.Npgsql"/> assembly).
/// </summary>
public sealed class QaDbContextFactory : IDesignTimeDbContextFactory<QaDbContext>
{
    public QaDbContext CreateDbContext(string[] args)
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        var cs = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
            ?? Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5432;Database=qa_designtime;Username=qa;Password=qa";
        var builder = new DbContextOptionsBuilder<QaDbContext>();
        builder.UseNpgsql(cs, n => n.MigrationsAssembly("QaAutomation.Migrations.Npgsql"));
        return new QaDbContext(builder.Options);
    }
}
