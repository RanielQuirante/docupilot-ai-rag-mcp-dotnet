using DocuPilot.Services.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace DocuPilot.Services;

/// <summary>
/// Composition root extensions for the Services layer (business logic).
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
        // Phase 2: document upload + library use case. Scoped to align with the
        // scoped repository / DbContext it depends on.
        services.AddScoped<IDocumentService, DocumentService>();
        return services;
    }
}
