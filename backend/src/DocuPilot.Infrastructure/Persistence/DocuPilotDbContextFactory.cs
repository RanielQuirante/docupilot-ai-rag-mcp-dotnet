using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DocuPilot.Infrastructure.Persistence;

/// <summary>
/// Design-time factory used by the EF Core tools (<c>dotnet ef migrations add</c> /
/// <c>database update</c>). It builds a <see cref="DocuPilotDbContext"/> without running
/// the API's startup pipeline (which would attempt the migrate-with-retry against a live
/// DB). The connection string here is only used to determine the provider for scaffolding
/// migrations — no database is contacted when generating a migration.
/// </summary>
public sealed class DocuPilotDbContextFactory : IDesignTimeDbContextFactory<DocuPilotDbContext>
{
    public DocuPilotDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DocuPilotDb")
            ?? "Server=localhost,1433;Database=DocuPilot;User Id=sa;Password=Your_strong_Password123;TrustServerCertificate=True";

        var options = new DbContextOptionsBuilder<DocuPilotDbContext>()
            .UseSqlServer(
                connectionString,
                sql => sql.MigrationsAssembly(typeof(DocuPilotDbContext).Assembly.GetName().Name))
            .Options;

        return new DocuPilotDbContext(options);
    }
}
