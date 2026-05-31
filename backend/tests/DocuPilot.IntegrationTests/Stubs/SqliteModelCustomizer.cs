using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace DocuPilot.IntegrationTests.Stubs;

/// <summary>
/// Test-only EF model customizer that runs AFTER the production <c>OnModelCreating</c> and strips the
/// SQL Server-specific <c>nvarchar(max)</c> column types so the schema is creatable on SQLite (which
/// rejects the <c>(max)</c> length token). SQLite ignores declared column types anyway (TEXT affinity),
/// so clearing the explicit type is lossless for the integration tests (DA-062). Production is untouched.
/// </summary>
public sealed class SqliteModelCustomizer : RelationalModelCustomizer
{
    public SqliteModelCustomizer(ModelCustomizerDependencies dependencies)
        : base(dependencies)
    {
    }

    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);

        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetProperties()))
        {
            var columnType = property.GetColumnType();
            if (columnType is not null && columnType.Contains("(max)", StringComparison.OrdinalIgnoreCase))
            {
                // Clear the SQL Server type; SQLite will use TEXT affinity for the string property.
                property.SetColumnType(null);
            }
        }
    }
}
