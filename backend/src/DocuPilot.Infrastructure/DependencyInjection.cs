using DocuPilot.Infrastructure.FileStorage;
using DocuPilot.Infrastructure.Persistence;
using DocuPilot.Repository;
using DocuPilot.Services.Abstractions;
using DocuPilot.Services.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocuPilot.Infrastructure;

/// <summary>
/// Composition root extensions for the Infrastructure layer: the EF Core DbContext,
/// the file-storage adapter, options binding, and (internally) the repositories.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers Infrastructure-layer services into the DI container.
    /// </summary>
    /// <param name="services">The DI service collection.</param>
    /// <param name="configuration">Application configuration root (connection strings, file-storage options).</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // EF Core DbContext. The SQL Server provider + migrations live with the context
        // (Infrastructure), and the migrations assembly is pinned to Infrastructure so
        // `dotnet ef` and the runtime migrator resolve the migration files here.
        var connectionString = configuration.GetConnectionString("DocuPilotDb");
        services.AddDbContext<DocuPilotDbContext>(options =>
            options.UseSqlServer(
                connectionString,
                sql => sql.MigrationsAssembly(typeof(DocuPilotDbContext).Assembly.GetName().Name)));

        // Expose the concrete context as the base DbContext so the provider-agnostic
        // Repository project (which references only Microsoft.EntityFrameworkCore and never
        // sees DocuPilotDbContext) can inject it (DA-011 §2.3/§2.7).
        services.AddScoped<DbContext>(sp => sp.GetRequiredService<DocuPilotDbContext>());

        // File-storage options + validation options both bind from the "FileStorage" section
        // (env keys FileStorage__RootPath / FileStorage__MaxBytes).
        services.Configure<FileStorageOptions>(configuration.GetSection(FileStorageOptions.SectionName));
        services.Configure<DocumentUploadOptions>(configuration.GetSection(DocumentUploadOptions.SectionName));

        // System clock for testable timestamp generation. LocalFileStorage (and other
        // Infrastructure timestamp consumers) depend on TimeProvider, so it MUST be
        // registered alongside the services that need it — registering it here keeps the
        // dependency with its consumer so every host calling AddInfrastructure (Api AND
        // Worker) gets it and the two composition roots can't drift (DA-021). TryAdd lets a
        // host or test substitute a fake clock by registering its own TimeProvider first.
        services.TryAddSingleton(TimeProvider.System);

        // Local-filesystem implementation of the IFileStorage port (defined in Services).
        services.AddSingleton<IFileStorage, LocalFileStorage>();

        // Data-access registration is an Infrastructure concern (Infrastructure owns the
        // DbContext that repositories depend on), so AddInfrastructure internally calls
        // AddRepositories(). Callers (Api/Worker Program.cs) never call it directly.
        services.AddRepositories();

        return services;
    }
}
