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
        // No registrations in Phase 1.5. Repository interfaces (Abstractions/) and their
        // implementations (Documents/, Workflows/, Audit/) are wired here in Phase 2+.
        return services;
    }
}
