using DocuPilot.Infrastructure.FileStorage;
using DocuPilot.Infrastructure.Llm;
using DocuPilot.Infrastructure.Persistence;
using DocuPilot.Infrastructure.TextExtraction;
using DocuPilot.Infrastructure.Vector;
using DocuPilot.Repository;
using DocuPilot.Services.Abstractions;
using DocuPilot.Services.Documents;
using DocuPilot.Services.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Qdrant.Client;

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

        // Extraction bounds (timeout / retry / max-chars) — env keys Extraction__* (DA-028).
        services.Configure<ExtractionOptions>(configuration.GetSection(ExtractionOptions.SectionName));

        // Phase 4: LLM client + classification bounds — env keys Llm__* (DA-036 wires these on
        // api AND worker). Bound here so both hosts get identical options.
        services.Configure<LlmOptions>(configuration.GetSection(LlmOptions.SectionName));

        // Phase 5: chunking / embedding / Qdrant bounds — env keys Chunking__* / Embedding__* /
        // Qdrant__* (DA-043 wires these on api AND worker). Bound here so both hosts get identical
        // options. ChunkingConfig backs the RecursiveCharacterChunker (registered in AddServices).
        services.Configure<ChunkingConfig>(configuration.GetSection(ChunkingConfig.SectionName));
        services.Configure<EmbeddingOptions>(configuration.GetSection(EmbeddingOptions.SectionName));
        services.Configure<QdrantOptions>(configuration.GetSection(QdrantOptions.SectionName));

        // Phase 6: semantic search bounds — env keys Search__* (DA-045). All keys have code
        // defaults, so search works with ZERO env changes (DevOps DA-048 is optional, docs-only).
        services.Configure<SearchOptions>(configuration.GetSection(SearchOptions.SectionName));

        // System clock for testable timestamp generation. LocalFileStorage (and other
        // Infrastructure timestamp consumers) depend on TimeProvider, so it MUST be
        // registered alongside the services that need it — registering it here keeps the
        // dependency with its consumer so every host calling AddInfrastructure (Api AND
        // Worker) gets it and the two composition roots can't drift (DA-021). TryAdd lets a
        // host or test substitute a fake clock by registering its own TimeProvider first.
        services.TryAddSingleton(TimeProvider.System);

        // Local-filesystem implementation of the IFileStorage port (defined in Services).
        services.AddSingleton<IFileStorage, LocalFileStorage>();

        // Text-extraction port implementations (Phase 3) + the resolver/dispatch entry point.
        // Stateless → singletons. Registered here in the shared extension so the Worker host
        // (DA-025) composes the exact same extractor set (no DA-021-style drift).
        services.AddSingleton<ITextExtractor, PlainTextExtractor>();
        services.AddSingleton<ITextExtractor, PdfTextExtractor>();
        services.AddSingleton<ITextExtractor, DocxTextExtractor>();
        services.AddSingleton<ITextExtractionService, TextExtractionService>();

        // Phase 4: the prompt library (editable embedded resources) — stateless singleton.
        services.AddSingleton<IPromptProvider, PromptProvider>();

        // Phase 4: the LLM client as a typed HttpClient (IHttpClientFactory) targeting the
        // in-network Ollama/OpenAI-compatible server. Base address + timeout from LlmOptions.
        // Registered here in the shared extension so the Worker host (DA-033) composes the exact
        // same client (no DA-021-style drift). The HttpClient.Timeout is set generously to the
        // configured per-call timeout (the orchestrator also applies a linked timeout CTS).
        var llmOptions = new LlmOptions();
        configuration.GetSection(LlmOptions.SectionName).Bind(llmOptions);
        services.AddHttpClient<ILlmClient, OllamaLlmClient>(client =>
        {
            client.BaseAddress = new Uri(llmOptions.BaseUrl);
            // A little headroom over the per-call timeout so the orchestrator's CTS fires first.
            client.Timeout = TimeSpan.FromSeconds(llmOptions.TimeoutSeconds + 30);
        });

        // Phase 5: the embedding client as a typed HttpClient (IHttpClientFactory) targeting the
        // in-network Ollama/OpenAI-compatible embedding server. A DEDICATED client distinct from the
        // LLM client (different model + endpoint, ADR §2); the OllamaLlmClient is left untouched.
        // Base address + timeout from EmbeddingOptions. Shared extension → api + worker compose the
        // exact same client (no DA-021 drift).
        var embeddingOptions = new EmbeddingOptions();
        configuration.GetSection(EmbeddingOptions.SectionName).Bind(embeddingOptions);
        services.AddHttpClient<IEmbeddingClient, OllamaEmbeddingClient>(client =>
        {
            client.BaseAddress = new Uri(embeddingOptions.BaseUrl);
            // A little headroom over the per-call timeout so the orchestrator's CTS fires first.
            client.Timeout = TimeSpan.FromSeconds(embeddingOptions.TimeoutSeconds + 15);
        });

        // Phase 5: the official Qdrant gRPC client (singleton — manages its own connection) + the
        // IVectorStore adapter over it. Host/port/TLS from QdrantOptions (in-network gRPC :6334).
        var qdrantOptions = new QdrantOptions();
        configuration.GetSection(QdrantOptions.SectionName).Bind(qdrantOptions);
        services.AddSingleton(_ => new QdrantClient(qdrantOptions.Host, qdrantOptions.GrpcPort, qdrantOptions.UseTls));
        services.AddSingleton<IVectorStore, QdrantVectorStore>();

        // Phase 5: the shared startup task that bootstraps the Qdrant collection on boot for BOTH
        // api and worker (create-if-absent + dim-validate from IEmbeddingClient.Dimensions, ADR §3).
        // Tolerant of Qdrant-not-ready (logs, does not crash-loop); fails loud on a dim mismatch.
        // Mirrors the DatabaseMigrator startup pattern.
        services.AddHostedService<QdrantCollectionBootstrapper>();

        // Data-access registration is an Infrastructure concern (Infrastructure owns the
        // DbContext that repositories depend on), so AddInfrastructure internally calls
        // AddRepositories(). Callers (Api/Worker Program.cs) never call it directly.
        services.AddRepositories();

        return services;
    }
}
