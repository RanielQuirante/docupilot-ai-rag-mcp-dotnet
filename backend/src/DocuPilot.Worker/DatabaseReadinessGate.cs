using DocuPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocuPilot.Worker;

/// <summary>
/// A boot-time DB-readiness probe (DA-044-D1). The API owns migrations
/// (<c>DatabaseMigrator.MigrateAsync</c> at API startup); the Worker must NOT run them (two writers
/// racing the same migration history is a bug). Instead the Worker WAITS until the API has finished:
/// the DB is reachable AND there are no pending migrations (the schema — including
/// <c>AuditLogs</c> — exists). Until then the Worker's poll loop must not tick, or its first 1–3
/// ticks log transient <c>Invalid object name 'AuditLogs'</c>.
/// </summary>
public interface IDatabaseReadinessProbe
{
    /// <summary>
    /// <c>true</c> once the database is reachable AND fully migrated (no pending migrations); else
    /// <c>false</c>. NEVER throws — a transient connection/schema fault is swallowed and reported as
    /// "not ready yet" so the gate keeps waiting rather than crashing the host.
    /// </summary>
    Task<bool> IsReadyAsync(CancellationToken ct);
}

/// <summary>
/// EF Core implementation of <see cref="IDatabaseReadinessProbe"/>. Resolves a fresh scoped
/// <see cref="DocuPilotDbContext"/> per probe (the Worker is a singleton; the DbContext is scoped),
/// then checks <see cref="Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade.CanConnectAsync"/>
/// AND <c>GetPendingMigrationsAsync</c> is empty. Reading the applied/pending migration sets queries
/// the <c>__EFMigrationsHistory</c> table, which only exists after the API has migrated — so any
/// fault here (DB warming up, history table absent) is treated as "not ready".
/// </summary>
public sealed class EfCoreDatabaseReadinessProbe : IDatabaseReadinessProbe
{
    private readonly IServiceScopeFactory _scopeFactory;

    public EfCoreDatabaseReadinessProbe(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task<bool> IsReadyAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DocuPilotDbContext>();

            // 1) Is SQL Server accepting connections yet?
            if (!await db.Database.CanConnectAsync(ct))
            {
                return false;
            }

            // 2) Has the API applied every migration? A non-empty pending set means the schema is
            // not in place yet (or the __EFMigrationsHistory table does not exist — querying it
            // throws, which the catch below maps to "not ready").
            var pending = await db.Database.GetPendingMigrationsAsync(ct);
            return !pending.Any();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // graceful shutdown — let the gate observe cancellation
        }
        catch
        {
            // DB still warming up / history table not created yet / transient fault. Report
            // "not ready" so the gate backs off and retries instead of crashing the host.
            return false;
        }
    }
}

/// <summary>
/// The bounded-backoff wait loop that gates the Worker's first real tick on a migrated DB
/// (DA-044-D1). Pure and dependency-light (an <see cref="IDatabaseReadinessProbe"/> + a
/// <see cref="Microsoft.Extensions.Logging.ILogger"/> + a configurable delay), so the wait logic is
/// unit-testable in isolation against a fake probe.
/// </summary>
public sealed class DatabaseReadinessGate
{
    private readonly IDatabaseReadinessProbe _probe;
    private readonly ILogger _logger;
    private readonly TimeSpan _pollDelay;

    /// <param name="probe">The readiness check (reachable + fully migrated).</param>
    /// <param name="logger">For boot-time progress/log lines.</param>
    /// <param name="pollDelay">Backoff between probes (default 2 s). Kept small so the Worker
    /// starts promptly once the API finishes migrating.</param>
    public DatabaseReadinessGate(IDatabaseReadinessProbe probe, ILogger logger, TimeSpan? pollDelay = null)
    {
        _probe = probe;
        _logger = logger;
        _pollDelay = pollDelay ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>
    /// Polls the probe until the DB is ready (reachable + fully migrated) or cancellation is
    /// requested. Robust: a never-ready DB does NOT crash — it keeps retrying with the backoff
    /// delay until <paramref name="ct"/> fires. Returns <c>true</c> when ready, <c>false</c> only if
    /// cancellation was requested before readiness (i.e. host shutdown during boot).
    /// </summary>
    public async Task<bool> WaitUntilReadyAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "Waiting for the database to be reachable and fully migrated before starting the poll loop " +
            "(the API owns migrations; the Worker waits). Probing every {DelaySeconds}s.",
            _pollDelay.TotalSeconds);

        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            attempt++;
            if (await _probe.IsReadyAsync(ct))
            {
                _logger.LogInformation(
                    "Database is ready (reachable + fully migrated) after {Attempts} probe(s); starting the poll loop.",
                    attempt);
                return true;
            }

            _logger.LogInformation(
                "Database not ready yet (attempt {Attempt}) — not reachable or migrations still pending. Retrying in {DelaySeconds}s...",
                attempt, _pollDelay.TotalSeconds);

            try
            {
                await Task.Delay(_pollDelay, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break; // graceful shutdown during the wait
            }
        }

        _logger.LogInformation("Database-readiness wait cancelled (host shutting down before the DB became ready).");
        return false;
    }
}
