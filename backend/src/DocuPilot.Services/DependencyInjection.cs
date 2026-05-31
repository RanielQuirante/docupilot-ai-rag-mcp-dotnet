using DocuPilot.Services.Abstractions;
using DocuPilot.Services.Audit;
using DocuPilot.Services.Dashboard;
using DocuPilot.Services.Documents;
using DocuPilot.Services.Rag;
using DocuPilot.Services.Search;
using DocuPilot.Services.Tools;
using DocuPilot.Services.Workflow;
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

        // Phase 5: the chunker — pure, deterministic, dependency-free CPU string logic. Lives in
        // Services (not Infrastructure, ADR §1). Stateless → singleton.
        services.AddSingleton<IChunkingService, RecursiveCharacterChunker>();

        // Phase 5: the reusable embedding orchestrator (claim Classified → GeneratingEmbeddings,
        // chunk, embed-all, Qdrant-first, transactional SQL+status persist + audit). The Worker host
        // (DA-040) resolves the SAME registration — keep it in this shared extension so the two
        // composition roots can't drift (lessons.md DA-021).
        services.AddScoped<IEmbeddingService, EmbeddingService>();

        // Phase 6: the semantic search orchestrator (embed query → vector search → group-by-doc →
        // rank → batch-hydrate). Read-only; reuses the already-registered IEmbeddingClient /
        // IVectorStore / repositories. Scoped to align with the scoped repositories/DbContext.
        services.AddScoped<ISearchService, SearchService>();

        // Phase 7: the RAG question-answering orchestrator (DA-049 — embed question → vector-search
        // top-k chunks → grounding short-circuit → hydrate → context builder → chat LLM in PROSE mode
        // → parse + not-found detection). Read-only; reuses the already-registered IEmbeddingClient /
        // IVectorStore / ILlmClient / IPromptProvider / repositories. Scoped to align with the scoped
        // repositories/DbContext. API-only — the Worker does NOT do RAG (ADR §7).
        services.AddScoped<IRagService, RagService>();

        // Phase 8 (DA-054): the workflow orchestrator (recommend [LLM JSON-mode] / create [validated
        // audited write] / list / complete). The single validated business layer the tools AND the
        // controllers call. API-only (the Worker does NOT do workflow). Scoped to align with the
        // scoped repositories/DbContext.
        services.AddScoped<IWorkflowService, WorkflowService>();

        // Phase 8: the MCP-style tool layer (ADR §2). The registry is a singleton (composed once from
        // the registered ITool set); the dispatcher is scoped (it audits via the scoped IUnitOfWork /
        // IAuditRepository). Each ITool is scoped (its handler delegates to scoped services/repos).
        services.AddScoped<ITool, SearchDocumentsTool>();
        services.AddScoped<ITool, GetDocumentByIdTool>();
        services.AddScoped<ITool, GetPendingDocumentsTool>();
        services.AddScoped<ITool, ExtractMetadataTool>();
        services.AddScoped<ITool, RecommendWorkflowTool>();
        services.AddScoped<ITool, CreateWorkflowTaskTool>();
        services.AddScoped<IToolRegistry, ToolRegistry>();
        services.AddScoped<IToolDispatcher, ToolDispatcher>();

        // Phase 8: the CONSTRAINED agent pipeline (recommend → create, both dispatched + audited).
        services.AddScoped<IAgentPipeline, AgentPipeline>();

        // Phase 9 (DA-058): the two additive READ-ONLY services. Dashboard stats composes three
        // aggregate repo queries; the audit-log list pages the global AuditLogs table. Both scoped to
        // align with the scoped repositories/DbContext. No audit write (pure reads).
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<IAuditLogService, AuditLogService>();

        return services;
    }
}
