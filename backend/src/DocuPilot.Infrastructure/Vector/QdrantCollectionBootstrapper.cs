using DocuPilot.Services.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DocuPilot.Infrastructure.Vector;

/// <summary>
/// Startup task (hosted service) that bootstraps the Qdrant collection on boot for BOTH the api and
/// the worker (registered in the SHARED <c>AddInfrastructure</c>, ADR §3/§8). Calls
/// <see cref="IVectorStore.EnsureCollectionAsync"/> with <see cref="IEmbeddingClient.Dimensions"/> —
/// the single source of truth for the vector size — so the dim comes from app/config, not YAML
/// (avoids the §2 dim-drift trap). Whoever wins creates it; the other no-ops (idempotent).
/// <para>
/// Mirrors the <c>DatabaseMigrator</c> retry/wait pattern but is TOLERANT of Qdrant-not-ready: on a
/// connectivity fault it logs and gives up <b>without crashing the host</b> (the per-tick
/// orchestrator handles Qdrant-down as Transient). A DIMENSION MISMATCH, however, fails loud — that
/// is a configuration error the operator must fix, not a transient condition.
/// </para>
/// </summary>
public sealed class QdrantCollectionBootstrapper : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<QdrantCollectionBootstrapper> _logger;
    private readonly int _maxAttempts;
    private readonly TimeSpan _delay;

    public QdrantCollectionBootstrapper(IServiceProvider services, ILogger<QdrantCollectionBootstrapper> logger)
        : this(services, logger, maxAttempts: 10, delay: TimeSpan.FromSeconds(3))
    {
    }

    // Ctor seam for tunable retry (used by tests).
    public QdrantCollectionBootstrapper(
        IServiceProvider services,
        ILogger<QdrantCollectionBootstrapper> logger,
        int maxAttempts,
        TimeSpan delay)
    {
        _services = services;
        _logger = logger;
        _maxAttempts = maxAttempts;
        _delay = delay;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;
        var vectorStore = sp.GetRequiredService<IVectorStore>();
        var embedder = sp.GetRequiredService<IEmbeddingClient>();
        var dimensions = embedder.Dimensions;

        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _logger.LogInformation(
                    "Ensuring Qdrant collection at {Dimensions} dimensions (attempt {Attempt}/{MaxAttempts})...",
                    dimensions, attempt, _maxAttempts);
                await vectorStore.EnsureCollectionAsync(dimensions, cancellationToken);
                _logger.LogInformation("Qdrant collection ensured.");
                return;
            }
            catch (InvalidOperationException ex)
            {
                // Dimension mismatch — a hard configuration error. Fail loud (crash the host so the
                // operator sees it), do NOT swallow as transient (ADR §2/§3).
                _logger.LogCritical(ex, "Qdrant collection dimension mismatch — refusing to start.");
                throw;
            }
            catch (VectorStoreUnavailableException ex) when (attempt < _maxAttempts)
            {
                _logger.LogWarning(
                    ex,
                    "Qdrant not ready (attempt {Attempt}/{MaxAttempts}). Retrying in {DelaySeconds}s...",
                    attempt, _maxAttempts, _delay.TotalSeconds);
                await Task.Delay(_delay, cancellationToken);
            }
            catch (VectorStoreUnavailableException ex)
            {
                // Tolerant of Qdrant-not-ready: log + give up WITHOUT crashing the host. The per-tick
                // orchestrator handles Qdrant-down as Transient and the collection is re-ensured on
                // the next boot / first use (ADR §3/§8). Defense-in-depth alongside the DevOps
                // healthcheck.
                _logger.LogWarning(
                    ex,
                    "Qdrant still unreachable after {MaxAttempts} attempts; continuing startup. The collection " +
                    "will be ensured when Qdrant becomes available (orchestrator treats Qdrant-down as Transient).",
                    _maxAttempts);
                return;
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
