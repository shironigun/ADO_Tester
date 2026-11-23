using Microsoft.Extensions.Logging.Abstractions;
using QaAutomation.Infrastructure.Data;
using QaAutomation.Infrastructure.TextSearch;

namespace QaAutomation.Tests;

/// <summary>
/// Verifies fresh PostgreSQL database initialization, seeding, and idempotency.
/// </summary>
public sealed class DatabaseInitializerTests
{
    [Fact]
    public void Builds_fresh_database_seeds_project_and_is_idempotent()
    {
        using var pg = PostgresTestDatabase.Create("initializer");
        using (var ctx = pg.CreateContext())
            new DatabaseInitializer(ctx, new PostgresFtsSearch(), NullLogger<DatabaseInitializer>.Instance).Initialize();

        using (var ctx = pg.CreateContext())
        {
            var project = Assert.Single(ctx.Projects);
            Assert.Equal(1, project.Id);
            Assert.Equal("Default Project", project.Name);
            Assert.Equal("", project.AdoOrganization);
            Assert.Equal("", project.AdoProject);
            Assert.Empty(ctx.TestSessions);
        }

        using (var ctx = pg.CreateContext())
            new DatabaseInitializer(ctx, new PostgresFtsSearch(), NullLogger<DatabaseInitializer>.Instance).Initialize();
        using var verify = pg.CreateContext();
        Assert.Single(verify.Projects);
    }
}
