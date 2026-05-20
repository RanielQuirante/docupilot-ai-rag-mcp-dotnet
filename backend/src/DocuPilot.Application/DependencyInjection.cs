using Microsoft.Extensions.DependencyInjection;

namespace DocuPilot.Application;

/// <summary>
/// Composition root extensions for the Application layer.
/// Phase 1 stub — registrations are added as use cases land in later phases.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers Application-layer services into the DI container.
    /// </summary>
    /// <param name="services">The DI service collection.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // No registrations in Phase 1. Use cases (MediatR handlers, validators, etc.)
        // are wired here as they are introduced in Phase 2+.
        return services;
    }
}
