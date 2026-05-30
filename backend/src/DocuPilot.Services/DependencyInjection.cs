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

        // Phase 3: the reusable processing orchestrator (state machine: claim → extract →
        // persist text + advance status + audit, transactionally). The Worker host (DA-025)
        // resolves the SAME registration — keep it in this shared extension so the two
        // composition roots can't drift (lessons.md DA-021).
        services.AddScoped<IDocumentProcessingService, DocumentProcessingService>();

        // Phase 4: the reusable classification + metadata orchestrator (claim TextExtracted →
        // Classifying, two LLM calls, validate/coerce, transactional persist + audit). The Worker
        // host (DA-033) resolves the SAME registration — keep it in this shared extension so the
        // two composition roots can't drift (lessons.md DA-021).
        services.AddScoped<IClassificationService, ClassificationService>();

        return services;
    }
}
