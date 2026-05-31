using System.Text.Json;
using DocuPilot.Models.Entities;
using DocuPilot.Models.Enums;
using DocuPilot.Repository.Abstractions;
using DocuPilot.Services.Abstractions;
using DocuPilot.Services.Documents;
using DocuPilot.Services.Tools;
using DocuPilot.Services.Workflow;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace DocuPilot.UnitTests.Workflow;

/// <summary>
/// The marquee Phase-8 safety assertion at the unit level (DA-054 / ADR §8 / QA DA-057 (b)+(d)): the
/// REAL <see cref="ToolDispatcher"/> → REAL <see cref="CreateWorkflowTaskTool"/> → REAL
/// <see cref="WorkflowService"/> path. A VALID create persists a WorkflowTask row AND an AuditLog (the
/// service's success audit) AND the dispatcher's ToolInvoked/ToolSucceeded framing — all through the
/// validated tool layer. A schema-invalid create is REJECTED by the dispatcher before the handler
/// runs — NO WorkflowTask row is ever staged (the AI cannot force a raw write). Repos +
/// <see cref="IUnitOfWork"/> are mocked; transactions run inline.
/// </summary>
public sealed class CreateWorkflowTaskSafetyTests
{
    private readonly Mock<IDocumentRepository> _documents = new();
    private readonly Mock<IDocumentClassificationRepository> _classifications = new();
    private readonly Mock<IExtractedMetadataRepository> _metadata = new();
    private readonly Mock<IDocumentTextRepository> _texts = new();
    private readonly Mock<IWorkflowTaskRepository> _tasks = new();
    private readonly Mock<IAuditRepository> _audit = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ILlmClient> _llm = new();
    private readonly Mock<IPromptProvider> _prompts = new();

    private readonly List<WorkflowTask> _stagedTasks = [];
    private readonly List<AuditLog> _writtenAudits = [];

    public CreateWorkflowTaskSafetyTests()
    {
        _unitOfWork.Setup(u => u.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((action, ct) => action(ct));
        _tasks.Setup(r => r.AddTrackedAsync(It.IsAny<WorkflowTask>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowTask, CancellationToken>((t, _) => _stagedTasks.Add(t))
            .Returns(Task.CompletedTask);
        _audit.Setup(r => r.AddAsync(It.IsAny<AuditLog>(), It.IsAny<CancellationToken>()))
            .Callback<AuditLog, CancellationToken>((a, _) => _writtenAudits.Add(a))
            .Returns(Task.CompletedTask);
    }

    private (ToolDispatcher Dispatcher, WorkflowService Service) Build()
    {
        var service = new WorkflowService(
            _documents.Object, _classifications.Object, _metadata.Object, _texts.Object,
            _tasks.Object, _audit.Object, _unitOfWork.Object, _llm.Object, _prompts.Object,
            TimeProvider.System, Options.Create(new LlmOptions()), Options.Create(new WorkflowOptions()),
            NullLogger<WorkflowService>.Instance);

        var tool = new CreateWorkflowTaskTool(service);
        var registry = new ToolRegistry(new ITool[] { tool });
        var dispatcher = new ToolDispatcher(registry, _audit.Object, _unitOfWork.Object, TimeProvider.System, NullLogger<ToolDispatcher>.Instance);
        return (dispatcher, service);
    }

    private static JsonElement Args(object value)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task ValidCreateThroughDispatcher_PersistsRow_AndWritesAudit()
    {
        var docId = Guid.CreateVersion7();
        _documents.Setup(r => r.GetByIdAsync(docId, It.IsAny<CancellationToken>())).ReturnsAsync(new Document
        {
            Id = docId, FileName = "c.pdf", ContentType = "application/pdf", FilePath = "k", SizeBytes = 1,
            Status = DocumentStatus.Classified, UploadedAt = DateTime.UtcNow,
        });

        var (dispatcher, _) = Build();
        var result = await dispatcher.InvokeAsync("create_workflow_task",
            Args(new { documentId = docId, taskType = "LegalReview", assignedTeam = "Legal", priority = "High", reason = "contract" }),
            CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Succeeded);

        // A WorkflowTasks row was staged (Status=Open), through the validated tool path.
        _stagedTasks.Should().ContainSingle();
        _stagedTasks[0].Status.Should().Be(WorkflowTaskStatus.Open);
        _stagedTasks[0].TaskType.Should().Be("LegalReview");

        // Audits written: the service's create audit + the dispatcher's ToolInvoked/ToolSucceeded.
        _writtenAudits.Select(a => a.Action).Should().Contain(nameof(AuditAction.ToolInvoked));
        _writtenAudits.Select(a => a.Action).Should().Contain(nameof(AuditAction.ToolSucceeded));
    }

    [Fact]
    public async Task SchemaInvalidCreateThroughDispatcher_Rejected_NoRowEverStaged()
    {
        var docId = Guid.CreateVersion7();
        _documents.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Document
        {
            Id = docId, FileName = "c.pdf", ContentType = "application/pdf", FilePath = "k", SizeBytes = 1,
            Status = DocumentStatus.Classified, UploadedAt = DateTime.UtcNow,
        });

        var (dispatcher, _) = Build();

        // Missing required 'priority' → schema-rejected BEFORE the handler runs.
        var result = await dispatcher.InvokeAsync("create_workflow_task",
            Args(new { documentId = docId, taskType = "LegalReview", assignedTeam = "Legal" }),
            CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Rejected);
        _stagedTasks.Should().BeEmpty(); // the AI could NOT force a write
        _writtenAudits.Select(a => a.Action).Should().ContainSingle().Which.Should().Be(nameof(AuditAction.ToolFailed));
    }
}
