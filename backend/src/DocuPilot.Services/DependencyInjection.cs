using Microsoft.Extensions.DependencyInjection;

namespace DocuPilot.Services;

/// <summary>
/// Composition root extensions for the Services layer (business logic).
/// Phase 1.5 stub — registrations are added as use-case services land in later phases.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers Services-layer (business-logic) services into the DI container.
    /// </summary>
    /// <param name="services">The DI service collection.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddServices(this IServiceCollection services)
    {
        // No registrations in Phase 1.5. Use-case services (and the external-service
        // ports under Abstractions/) are wired here as they are introduced in Phase 2+.
        return services;
    }
}
