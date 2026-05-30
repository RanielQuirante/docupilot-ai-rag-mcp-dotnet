using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DocuPilot.Infrastructure.Persistence;

/// <summary>
/// Applies EF Core migrations on startup, wrapped in a retry/wait loop so a fresh-volume
/// SQL Server that needs ~30–60 s to accept connections does NOT crash-loop the API
/// (DA-015 §6, constraint #10). <c>depends_on</c> in compose does not wait for readiness,
/// so this app-side retry is mandatory.
///
/// POC-only: auto-migrate-on-boot is the simplest "fresh docker compose up just works"
/// path. Production would gate migrations as a separate step (Phase-9 hardening).
/// </summary>
public static class DatabaseMigrator
{
    /// <summary>
    /// Applies pending migrations (creating the database if absent), retrying transient
    /// connection failures while SQL Server warms up.
    /// </summary>
    /// <param name="services">The application's root service provider.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="maxAttempts">Maximum number of attempts.</param>
    /// <param name="delay">Delay between attempts (default 5 s → ~60 s total over 12 attempts).</param>
    public static async Task MigrateAsync(
        IServiceProvider services,
        CancellationToken ct = default,
        int maxAttempts = 12,
        TimeSpan? delay = null)
    {
        var waitBetween = delay ?? TimeSpan.FromSeconds(5);
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseMigrator));
        var dbContext = sp.GetRequiredService<DocuPilotDbContext>();

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                logger.LogInformation("Applying database migrations (attempt {Attempt}/{MaxAttempts})...", attempt, maxAttempts);
                await dbContext.Database.MigrateAsync(ct);
                logger.LogInformation("Database migrations applied successfully.");
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && !ct.IsCancellationRequested)
            {
                logger.LogWarning(
                    ex,
                    "Database not ready (attempt {Attempt}/{MaxAttempts}). Retrying in {DelaySeconds}s...",
                    attempt,
                    maxAttempts,
                    waitBetween.TotalSeconds);
                await Task.Delay(waitBetween, ct);
            }
        }

        // Final attempt outside the catch so a persistent failure surfaces (and crashes
        // the app deliberately — a never-ready DB is a hard error, not a swallowed one).
        logger.LogInformation("Applying database migrations (final attempt {MaxAttempts}/{MaxAttempts})...", maxAttempts, maxAttempts);
        await dbContext.Database.MigrateAsync(ct);
        logger.LogInformation("Database migrations applied successfully on the final attempt.");
    }
}
