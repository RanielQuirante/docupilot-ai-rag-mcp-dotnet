using System.Text.Json;
using DocuPilot.Models.Entities;
using DocuPilot.Models.Enums;
using DocuPilot.Repository.Abstractions;
using DocuPilot.Services.Tools;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DocuPilot.UnitTests.Workflow;

/// <summary>
/// Unit tests for <see cref="ToolDispatcher"/> — the Phase-8 safety core (DA-054 / ADR §8). Asserts
/// the audit-every-call guarantee + the reject-bad-args-with-no-write rule: EVERY successful call
/// writes ToolInvoked + ToolSucceeded; a failing handler writes ToolInvoked + ToolFailed; an unknown
/// tool writes ONLY ToolFailed (no handler); schema-invalid args are rejected BEFORE the handler runs
/// (no handler call) with a ToolFailed audit and no ToolInvoked. The <see cref="IUnitOfWork"/> /
/// <see cref="IAuditRepository"/> are mocked; the action runs inline so audits are captured.
/// </summary>
public sealed class ToolDispatcherTests
{
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IAuditRepository> _audit = new();
    private readonly List<AuditLog> _written = [];

    public ToolDispatcherTests()
    {
        _unitOfWork.Setup(u => u.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((action, ct) => action(ct));
        _audit.Setup(r => r.AddAsync(It.IsAny<AuditLog>(), It.IsAny<CancellationToken>()))
            .Callback<AuditLog, CancellationToken>((a, _) => _written.Add(a))
            .Returns(Task.CompletedTask);
    }

    private ToolDispatcher CreateSut(params ITool[] tools)
    {
        var registry = new ToolRegistry(tools);
        return new ToolDispatcher(registry, _audit.Object, _unitOfWork.Object, TimeProvider.System, NullLogger<ToolDispatcher>.Instance);
    }

    private static JsonElement Args(object value)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return doc.RootElement.Clone();
    }

    private IReadOnlyList<string> Actions() => _written.Select(a => a.Action).ToList();

    [Fact]
    public async Task InvokeAsync_SuccessfulCall_WritesToolInvokedThenToolSucceeded()
    {
        var docId = Guid.CreateVersion7();
        var tool = new FakeTool("ok_tool", ToolResult.Succeeded(new { ok = true }), requireDocId: true);
        var dispatcher = CreateSut(tool);

        var result = await dispatcher.InvokeAsync("ok_tool", Args(new { documentId = docId }), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Succeeded);
        Actions().Should().ContainInOrder(nameof(AuditAction.ToolInvoked), nameof(AuditAction.ToolSucceeded));
        tool.CallCount.Should().Be(1);
        // EntityId on the audits is the args' documentId.
        _written.Should().OnlyContain(a => a.EntityId == docId && a.EntityName == "WorkflowTool");
    }

    [Fact]
    public async Task InvokeAsync_FailingHandlerOutcome_WritesToolInvokedThenToolFailed()
    {
        var tool = new FakeTool("nf_tool", ToolResult.NotFound("nope"), requireDocId: false);
        var dispatcher = CreateSut(tool);

        var result = await dispatcher.InvokeAsync("nf_tool", Args(new { }), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.NotFound);
        Actions().Should().ContainInOrder(nameof(AuditAction.ToolInvoked), nameof(AuditAction.ToolFailed));
        tool.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task InvokeAsync_HandlerThrows_WritesToolFailed_AndRejects()
    {
        var tool = new ThrowingTool("boom_tool");
        var dispatcher = CreateSut(tool);

        var result = await dispatcher.InvokeAsync("boom_tool", Args(new { }), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Rejected);
        Actions().Should().Contain(nameof(AuditAction.ToolFailed));
    }

    [Fact]
    public async Task InvokeAsync_UnknownTool_WritesOnlyToolFailed_NoHandlerNoWriteBeyondAudit()
    {
        var tool = new FakeTool("known", ToolResult.Succeeded(new { }), requireDocId: false);
        var dispatcher = CreateSut(tool);

        var result = await dispatcher.InvokeAsync("does_not_exist", Args(new { }), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Rejected);
        Actions().Should().ContainSingle().Which.Should().Be(nameof(AuditAction.ToolFailed));
        tool.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task InvokeAsync_SchemaInvalidArgs_RejectedBeforeHandler_ToolFailed_NoToolInvoked()
    {
        // Tool requires a GUID documentId; we pass a non-GUID → rejected pre-execution.
        var tool = new FakeTool("needs_guid", ToolResult.Succeeded(new { }), requireDocId: true);
        var dispatcher = CreateSut(tool);

        var result = await dispatcher.InvokeAsync("needs_guid", Args(new { documentId = "not-a-guid" }), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Rejected);
        tool.CallCount.Should().Be(0); // handler NEVER ran
        Actions().Should().ContainSingle().Which.Should().Be(nameof(AuditAction.ToolFailed));
        Actions().Should().NotContain(nameof(AuditAction.ToolInvoked));
    }

    // ---- fakes ----

    private sealed class FakeTool : ITool
    {
        private readonly ToolResult _result;

        public FakeTool(string name, ToolResult result, bool requireDocId)
        {
            Name = name;
            _result = result;
            Schema = requireDocId
                ? new ToolInputSchema(new ToolField("documentId", ToolFieldType.Guid, Required: true))
                : new ToolInputSchema();
        }

        public string Name { get; }

        public string Description => "fake";

        public ToolInputSchema Schema { get; }

        public int CallCount { get; private set; }

        public Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(_result);
        }
    }

    private sealed class ThrowingTool : ITool
    {
        public ThrowingTool(string name) => Name = name;

        public string Name { get; }

        public string Description => "throws";

        public ToolInputSchema Schema { get; } = new();

        public Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }
}
