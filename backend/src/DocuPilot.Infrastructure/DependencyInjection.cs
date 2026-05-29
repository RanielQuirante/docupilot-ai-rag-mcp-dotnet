using DocuPilot.Repository;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DocuPilot.Infrastructure;

/// <summary>
/// Composition root extensions for the Infrastructure layer.
/// Phase 1 stub — registrations (DbContext, vector store, LLM client, file storage)
/// are added in later phases.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers Infrastructure-layer services into the DI container.
    /// </summary>
    /// <param name="services">The DI service collection.</param>
    /// <param name="configuration">Application configuration root (used by later phases for connection strings, etc.).</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Data-access registration is an Infrastructure concern (Infrastructure owns the
        // DbContext that repositories depend on), so AddInfrastructure internally calls
        // AddRepositories(). Callers (Api/Worker Program.cs) never call AddRepositories()
        // directly — they only call AddServices() + AddInfrastructure(...).
        services.AddRepositories();

        // No further registrations in Phase 1.5. DocuPilotDbContext, IFileStorage,
        // IVectorStore, and ILlmClient land in later phases.
        _ = configuration;
        return services;
    }
}
