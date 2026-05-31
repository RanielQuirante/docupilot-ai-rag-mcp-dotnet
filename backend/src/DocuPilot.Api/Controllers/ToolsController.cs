using DocuPilot.Models.Contracts;
using DocuPilot.Services.Tools;
using Microsoft.AspNetCore.Mvc;

namespace DocuPilot.Api.Controllers;

/// <summary>
/// Phase-8 tool introspection endpoint (DA-054). Exposes the registered tool definitions
/// (<c>{name, description, inputSchema}</c>, spec §5.12) so a client/agent can discover the controlled
/// tool catalogue. Read-only. There is deliberately NO generic <c>POST /api/tools/{name}</c> invoke
/// surface (ADR §6 / PM Q3) — the typed <c>recommend</c>/<c>workflow-tasks</c>/<c>agent</c> endpoints
/// each dispatch through the validated, audited tool layer internally.
/// </summary>
[ApiController]
[Route("api/tools")]
public sealed class ToolsController : ControllerBase
{
    private readonly IToolRegistry _registry;

    public ToolsController(IToolRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>Returns the registered tool definitions.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ToolDefinitionDto>), StatusCodes.Status200OK)]
    public IActionResult List()
    {
        var tools = _registry.List()
            .Select(t => new ToolDefinitionDto(t.Name, t.Description, t.InputSchema))
            .ToList();
        return Ok(tools);
    }
}
