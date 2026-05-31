using DocuPilot.Api.Controllers;
using DocuPilot.Models.Contracts;
using DocuPilot.Models.Entities;
using DocuPilot.Models.Enums;
using DocuPilot.Repository.Abstractions;
using DocuPilot.Services.Abstractions;
using DocuPilot.Services.Documents;
using DocuPilot.Services.Tools;
using DocuPilot.Services.Workflow;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace DocuPilot.UnitTests.Workflow;

/// <summary>
/// DA-057-D1: <c>WorkflowController.CreateTask</c> status-code mapping. A non-existent documentId —
/// INCLUDING the all-zeros <see cref="Guid.Empty"/> — must return <c>404</c> (document not found),
/// CONSISTENT with any other missing document, rather than the old <c>400</c> short-circuit. A
/// genuine input-validation failure (off-enum priority, missing required field) must STILL return
/// <c>400</c>. In BOTH cases the SAFETY invariant holds: NO <c>WorkflowTasks</c> row is written (a
/// <c>ToolFailed</c> audit is fine). Wires the REAL dispatcher → REAL tool → REAL service with mocked
/// repos so the controller's outcome→status mapping is exercised through the production path.
/// </summary>
public sealed class WorkflowControllerCreateTaskTests
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

    public WorkflowControllerCreateTaskTests()
    {
        _unitOfWork
            .Setup(u => u.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((action, ct) => action(ct));
        _tasks
            .Setup(r => r.AddTrackedAsync(It.IsAny<WorkflowTask>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowTask, CancellationToken>((t, _) => _stagedTasks.Add(t))
            .Returns(Task.CompletedTask);
        _audit
            .Setup(r => r.AddAsync(It.IsAny<AuditLog>(), It.IsAny<CancellationToken>()))
            .Callback<AuditLog, CancellationToken>((a, _) => _writtenAudits.Add(a))
            .Returns(Task.CompletedTask);
    }

    private WorkflowController BuildController()
    {
        var service = new WorkflowService(
            _documents.Object, _classifications.Object, _metadata.Object, _texts.Object,
            _tasks.Object, _audit.Object, _unitOfWork.Object, _llm.Object, _prompts.Object,
            TimeProvider.System, Options.Create(new LlmOptions()), Options.Create(new WorkflowOptions()),
            NullLogger<WorkflowService>.Instance);

        var tool = new CreateWorkflowTaskTool(service);
        var registry = new ToolRegistry(new ITool[] { tool });
        var dispatcher = new ToolDispatcher(
            registry, _audit.Object, _unitOfWork.Object, TimeProvider.System, NullLogger<ToolDispatcher>.Instance);

        return new WorkflowController(service, dispatcher)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private static int StatusOf(IActionResult result) => result switch
    {
        ObjectResult o => o.StatusCode ?? 0,
        StatusCodeResult s => s.StatusCode,
        _ => 0,
    };

    [Fact]
    public async Task CreateTask_AllZerosGuid_Returns404_NotFound_AndWritesNoRow()
    {
        // Guid.Empty is a syntactically valid GUID naming a document that does not exist.
        _documents
            .Setup(r => r.GetByIdAsync(Guid.Empty, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Document?)null);

        var controller = BuildController();
        var request = new CreateWorkflowTaskRequest(Guid.Empty, "LegalReview", "Legal", "High", "review");

        var result = await controller.CreateTask(request, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound, "an all-zeros (missing) documentId is 404, not 400");
        _stagedTasks.Should().BeEmpty("SAFETY: no row may be written for a missing document");
    }

    [Fact]
    public async Task CreateTask_NonExistentGuid_Returns404_NotFound_AndWritesNoRow()
    {
        var missing = Guid.CreateVersion7();
        _documents
            .Setup(r => r.GetByIdAsync(missing, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Document?)null);

        var controller = BuildController();
        var request = new CreateWorkflowTaskRequest(missing, "LegalReview", "Legal", "High", "review");

        var result = await controller.CreateTask(request, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
        _stagedTasks.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateTask_OffEnumPriority_Returns400_BadRequest_AndWritesNoRow()
    {
        // The document EXISTS so we isolate the validation failure to the bad priority.
        var docId = Guid.CreateVersion7();
        _documents
            .Setup(r => r.GetByIdAsync(docId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Document
            {
                Id = docId, FileName = "c.pdf", ContentType = "application/pdf", FilePath = "k", SizeBytes = 1,
                Status = DocumentStatus.Classified, UploadedAt = DateTime.UtcNow,
            });

        var controller = BuildController();
        var request = new CreateWorkflowTaskRequest(docId, "LegalReview", "Legal", "Critical", "review"); // off-enum

        var result = await controller.CreateTask(request, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest, "an off-enum priority is malformed input → 400");
        _stagedTasks.Should().BeEmpty("SAFETY: a rejected create writes no row");
    }

    [Fact]
    public async Task CreateTask_MissingRequiredField_Returns400_BadRequest_AndWritesNoRow()
    {
        var docId = Guid.CreateVersion7();
        _documents
            .Setup(r => r.GetByIdAsync(docId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Document
            {
                Id = docId, FileName = "c.pdf", ContentType = "application/pdf", FilePath = "k", SizeBytes = 1,
                Status = DocumentStatus.Classified, UploadedAt = DateTime.UtcNow,
            });

        var controller = BuildController();
        // Missing required 'assignedTeam' (null) → schema/validation reject → 400.
        var request = new CreateWorkflowTaskRequest(docId, "LegalReview", null, "High", "review");

        var result = await controller.CreateTask(request, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
        _stagedTasks.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateTask_NullBody_Returns400_BadRequest()
    {
        var controller = BuildController();

        var result = await controller.CreateTask(null!, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest, "a missing request body is malformed input → 400");
        _stagedTasks.Should().BeEmpty();
    }
}
