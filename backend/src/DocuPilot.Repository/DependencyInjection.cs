using DocuPilot.Repository.Abstractions;
using DocuPilot.Repository.Audit;
using DocuPilot.Repository.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace DocuPilot.Repository;

/// <summary>
/// Composition root extensions for the Repository layer (all DB communication).
/// Phase 1.5 stub — repository registrations are added as repositories land in later phases.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers Repository-layer (data-access) services into the DI container.
    /// Called internally by <c>AddInfrastructure(...)</c> — callers never invoke it directly,
    /// keeping data-access registration an Infrastructure concern (Infrastructure owns the DbContext).
    /// </summary>
    /// <param name="services">The DI service collection.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddRepositories(this IServiceCollection services)
    {
        // Phase 2: the Documents data-access seam. The repository depends on the base
        // EF DbContext (registered by Infrastructure as the concrete DocuPilotDbContext),
        // which keeps this project provider-agnostic (DA-011 §2.3/§2.7).
        services.AddScoped<IDocumentRepository, DocumentRepository>();

        // Phase 3 data-access seams: extracted-text (upsert-by-DocumentId) + audit trail,
        // plus the transactional commit seam the processing orchestrator uses to write
        // status + text + audit atomically.
        services.AddScoped<IDocumentTextRepository, DocumentTextRepository>();
        services.AddScoped<IAuditRepository, AuditRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        return services;
    }
}
