using System.Reflection;
using Microsoft.AspNetCore.Mvc;

namespace DocuPilot.Api.Controllers;

/// <summary>
/// Phase 1 health endpoint. Returns a static 200 with a small JSON payload so
/// the API, the Angular SPA, and (later) Docker healthchecks have a cheap
/// liveness signal. No dependency checks (SQL Server / Qdrant / Ollama) are
/// performed here — those land in Phase 2 once the underlying services are
/// actually wired in.
/// </summary>
[ApiController]
[Route("health")]
public sealed class HealthController : ControllerBase
{
    private const string ServiceName = "DocuPilot.Api";
    private const string FallbackVersion = "0.1.0";

    private static readonly string ResolvedVersion = ReadInformationalVersion();

    private readonly TimeProvider _timeProvider;

    public HealthController(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Liveness probe. Always returns 200 in Phase 1.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(HealthResponse), StatusCodes.Status200OK)]
    public ActionResult<HealthResponse> Get()
    {
        var response = new HealthResponse(
            Status: "healthy",
            Service: ServiceName,
            Version: ResolvedVersion,
            Timestamp: _timeProvider.GetUtcNow().ToString("o"));

        return Ok(response);
    }

    private static string ReadInformationalVersion()
    {
        var informational = Assembly
            .GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return FallbackVersion;
        }

        // MSBuild can append a '+<sourceRevisionId>' suffix when SourceLink is on.
        // Strip it so the /health payload is a clean semver string.
        var plusIndex = informational.IndexOf('+', StringComparison.Ordinal);
        return plusIndex >= 0 ? informational[..plusIndex] : informational;
    }
}

/// <summary>
/// Wire-shape for <c>GET /health</c>. Defined alongside the controller because
/// it is a thin API-layer DTO with no place in Application or Domain.
/// </summary>
/// <param name="Status">Always <c>"healthy"</c> in Phase 1.</param>
/// <param name="Service">Logical service name (matches the assembly).</param>
/// <param name="Version">Service informational version (e.g. <c>0.1.0</c>).</param>
/// <param name="Timestamp">ISO 8601 UTC round-trip ("o") format.</param>
public sealed record HealthResponse(string Status, string Service, string Version, string Timestamp);
