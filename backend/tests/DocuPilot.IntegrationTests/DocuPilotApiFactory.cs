using DocuPilot.IntegrationTests.Stubs;
using DocuPilot.Infrastructure.Persistence;
using DocuPilot.Infrastructure.Vector;
using DocuPilot.Services.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace DocuPilot.IntegrationTests;

/// <summary>
/// The DA-062 integration-test host. A custom <see cref="WebApplicationFactory{TEntryPoint}"/> that boots the
/// REAL <c>DocuPilot.Api</c> pipeline (controllers, services, repositories, EF model) but with the external
/// dependencies replaced so NO SQL Server / Qdrant / Ollama is required:
/// <list type="bullet">
///   <item>The SQL Server <see cref="DocuPilotDbContext"/> registration is swapped for <b>SQLite</b> over a
///   single in-memory connection kept open for the factory's lifetime, so the real EF model + repositories
///   are exercised against a real (if ephemeral) relational schema. The provider-agnostic
///   <c>DatabaseMigrator</c> creates that schema via <c>EnsureCreated</c> (the committed migrations are SQL
///   Server-specific, so they are intentionally not applied here).</item>
///   <item>The three external-service ports (<see cref="ILlmClient"/>, <see cref="IEmbeddingClient"/>,
///   <see cref="IVectorStore"/>) are replaced with the deterministic <see cref="Stubs"/> so the AI paths
///   (search / ask / recommend) are exercised with scripted, network-free responses.</item>
///   <item>The <see cref="QdrantCollectionBootstrapper"/> hosted service is removed so startup does not try to
///   reach Qdrant.</item>
/// </list>
/// The stubs are exposed as instances so a test can script the exact response it needs.
/// </summary>
public sealed class DocuPilotApiFactory : WebApplicationFactory<Program>
{
    // A SQLite in-memory database lives only while at least one connection to it is open. We open one
    // here and keep it open for the whole factory lifetime so the schema + seeded rows survive across the
    // many short-lived DbContext scopes the API opens per request.
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    // A per-factory temp directory so LocalFileStorage writes uploaded bytes somewhere valid and
    // cross-platform (the production /app/files default is a Linux container path).
    private readonly string _fileRoot = Path.Combine(Path.GetTempPath(), "docupilot-it-" + Guid.NewGuid().ToString("N"));

    public StubEmbeddingClient EmbeddingClient { get; } = new();

    public StubVectorStore VectorStore { get; } = new();

    public StubLlmClient LlmClient { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // "Testing" disables the Development-only Swagger branch and keeps the pipeline lean.
        builder.UseEnvironment("Testing");
        _connection.Open();

        // Point file storage at a writable temp dir (overrides the /app/files container default).
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileStorage:RootPath"] = _fileRoot,
            });
        });

        builder.ConfigureServices(services =>
        {
            // ---- Swap the DbContext provider: SQL Server → SQLite (shared open in-memory connection). ----
            // AddDbContext registers several descriptors that pin the SQL Server provider — the options,
            // the (EF 10) IDbContextOptionsConfiguration<T>, and the context itself. Re-calling AddDbContext
            // without removing them all leaves BOTH providers registered ("Only a single database provider
            // can be registered" — EF resolves the wrong one). Strip every DbContext-related descriptor for
            // our context first, then register the SQLite one.
            var efDescriptors = services
                .Where(d =>
                    d.ServiceType == typeof(DocuPilotDbContext)
                    || d.ServiceType == typeof(DbContextOptions)
                    || d.ServiceType == typeof(DbContextOptions<DocuPilotDbContext>)
                    || (d.ServiceType.IsGenericType
                        && d.ServiceType.GetGenericArguments().Contains(typeof(DocuPilotDbContext))))
                .ToList();
            foreach (var descriptor in efDescriptors)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<DocuPilotDbContext>(options => options
                .UseSqlite(_connection)
                // Strip SQL Server's nvarchar(max) column types so the schema is creatable on SQLite.
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>());

            // ---- Replace the external-service ports with the deterministic stubs (no network). ----
            // The Ollama clients are registered as typed HttpClients and Qdrant as a singleton; removing the
            // service descriptors and adding the stub singletons makes the stubs authoritative.
            RemoveAll<IEmbeddingClient>(services);
            RemoveAll<ILlmClient>(services);
            RemoveAll<IVectorStore>(services);
            services.AddSingleton<IEmbeddingClient>(EmbeddingClient);
            services.AddSingleton<ILlmClient>(LlmClient);
            services.AddSingleton<IVectorStore>(VectorStore);

            // ---- Neutralize the Qdrant bootstrapper so the test host boots without reaching Qdrant. ----
            // Remove ONLY the bootstrapper descriptor (NOT all IHostedService — the framework's own
            // GenericWebHostService that runs the test server is also an IHostedService).
            var bootstrapper = services
                .Where(d => d.ServiceType == typeof(IHostedService)
                    && d.ImplementationType == typeof(QdrantCollectionBootstrapper))
                .ToList();
            foreach (var descriptor in bootstrapper)
            {
                services.Remove(descriptor);
            }
        });
    }

    /// <summary>Opens a DbContext bound to the same in-memory SQLite database for direct test seeding/asserts.</summary>
    public DocuPilotDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<DocuPilotDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new DocuPilotDbContext(options);
    }

    private static void RemoveAll<T>(IServiceCollection services) => services.RemoveAll(typeof(T));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
            try
            {
                if (Directory.Exists(_fileRoot))
                {
                    Directory.Delete(_fileRoot, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup; a leftover temp dir is harmless.
            }
        }
    }
}
