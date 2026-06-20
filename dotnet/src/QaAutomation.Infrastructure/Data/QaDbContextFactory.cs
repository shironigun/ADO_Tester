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
        var options = new DbContextOptionsBuilder<QaDbContext>()
            .UseSqlite("Data Source=qa_designtime.db")
            .Options;
        return new QaDbContext(options);
    }
}
