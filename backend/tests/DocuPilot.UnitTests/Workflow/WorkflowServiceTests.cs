using DocuPilot.Models.Entities;
using DocuPilot.Models.Enums;
using DocuPilot.Repository.Abstractions;
using DocuPilot.Services.Abstractions;
using DocuPilot.Services.Documents;
using DocuPilot.Services.Workflow;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace DocuPilot.UnitTests.Workflow;

/// <summary>
/// Unit tests for <see cref="WorkflowService"/> — the Phase-8 orchestrator (DA-054). The
/// <see cref="ILlmClient"/> is STUBBED (no real Ollama) and all repos + <see cref="IUnitOfWork"/> are
/// mocked. Covers: recommend (invoice-ish classification → recommendation; priority coercion of
/// off-values → Normal; LLM-down → Unavailable/503; doc-not-found → 404; not-classified → 409);
/// create (valid → row AND AuditLog written in ONE transaction; invalid input → Invalid/400, NOTHING
/// written; doc-not-found → 404); list (status filter passthrough); complete (sets CompletedAt;
/// double-complete → 409).
/// </summary>
public sealed class WorkflowServiceTests
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

    public WorkflowServiceTests()
    {
        // Run the transactional action inline so staged repo writes "happen" against the mocks.
        _unitOfWork.Setup(u => u.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((action, ct) => action(ct));

        _prompts.Setup(p => p.BuildWorkflowRecommendationPrompt(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns("PROMPT");
    }

    private WorkflowService CreateSut(WorkflowOptions? options = null) => new(
        _documents.Object,
        _classifications.Object,
        _metadata.Object,
        _texts.Object,
        _tasks.Object,
        _audit.Object,
        _unitOfWork.Object,
        _llm.Object,
        _prompts.Object,
        TimeProvider.System,
        Options.Create(new LlmOptions()),
        Options.Create(options ?? new WorkflowOptions()),
        NullLogger<WorkflowService>.Instance);

    private static Document Doc(Guid id) => new()
    {
        Id = id,
        FileName = "invoice.pdf",
        ContentType = "application/pdf",
        FilePath = "k.pdf",
        SizeBytes = 1,
        Status = DocumentStatus.Classified,
        UploadedAt = DateTime.UtcNow,
    };

    private static DocumentClassification Classification(Guid docId, DocumentCategory cat) => new()
    {
        Id = Guid.CreateVersion7(),
        DocumentId = docId,
        Classification = cat,
        Confidence = 0.9m,
        CreatedAt = DateTime.UtcNow,
    };

    private void SetupLlm(string content) =>
        _llm.Setup(l => l.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse(content, "llama3.2:3b", TimeSpan.Zero));

    // ---- RecommendAsync ----

    [Fact]
    public async Task RecommendAsync_InvoiceClassification_ReturnsRecommendation_JsonModeTemp0()
    {
        var docId = Guid.CreateVersion7();
        _documents.Setup(r => r.GetByIdAsync(docId, It.IsAny<CancellationToken>())).ReturnsAsync(Doc(docId));
        _classifications.Setup(r => r.GetByDocumentIdAsync(docId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Classification(docId, DocumentCategory.Invoice));

        LlmRequest? captured = null;
        _llm.Setup(l => l.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LlmRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new LlmResponse(
                "{\"recommendedWorkflow\":\"FinanceApproval\",\"nextStep\":\"Route to finance\",\"priority\":\"Normal\",\"reason\":\"It is an invoice.\"}",
                "llama3.2:3b", TimeSpan.Zero));

        var outcome = await CreateSut().RecommendAsync(docId, CancellationToken.None);

        outcome.Kind.Should().Be(RecommendOutcomeKind.Recommendation);
        outcome.Recommendation!.RecommendedWorkflow.Should().Be("FinanceApproval");
        outcome.Recommendation.Priority.Should().Be(WorkflowPriority.Normal);
        // JSON-mode posture (the Phase-4 classification call, NOT Phase-7 prose).
        captured!.JsonMode.Should().BeTrue();
        captured.Temperature.Should().Be(0);
    }

    [Fact]
    public async Task RecommendAsync_OffValuePriority_CoercedToNormal()
    {
        var docId = Guid.CreateVersion7();
        _documents.Setup(r => r.GetByIdAsync(docId, It.IsAny<CancellationToken>())).ReturnsAsync(Doc(docId));
        _classifications.Setup(r => r.GetByDocumentIdAsync(docId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Classification(docId, DocumentCategory.Contract));
        SetupLlm("{\"recommendedWorkflow\":\"LegalReview\",\"nextStep\":\"x\",\"priority\":\"SUPER-URGENT\",\"reason\":\"y\"}");

        var outcome = await CreateSut().RecommendAsync(docId, CancellationToken.None);

        outcome.Recommendation!.Priority.Should().Be(WorkflowPriority.Normal);
    }

    [Fact]
    public async Task RecommendAsync_UnparseableResponse_ReturnsSafeDefault()
    {
        var docId = Guid.CreateVersion7();
        _documents.Setup(r => r.GetByIdAsync(docId, It.IsAny<CancellationToken>())).ReturnsAsync(Doc(docId));
        _classifications.Setup(r => r.GetByDocumentIdAsync(docId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Classification(docId, DocumentCategory.Invoice));
        SetupLlm("the model rambled with no json");

        var outcome = await CreateSut().RecommendAsync(docId, CancellationToken.None);

        outcome.Kind.Should().Be(RecommendOutcomeKind.Recommendation);
        outcome.Recommendation!.RecommendedWorkflow.Should().Be("Manual Review");
        outcome.Recommendation.Priority.Should().Be(WorkflowPriority.Normal);
    }

    [Fact]
    public async Task RecommendAsync_LlmDown_ReturnsUnavailable()
    {
        var docId = Guid.CreateVersion7();
        _documents.Setup(r => r.GetByIdAsync(docId, It.IsAny<CancellationToken>())).ReturnsAsync(Doc(docId));
        _classifications.Setup(r => r.GetByDocumentIdAsync(docId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Classification(docId, DocumentCategory.Invoice));
        _llm.Setup(l => l.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LlmUnavailableException("ollama down"));

        var outcome = await CreateSut().RecommendAsync(docId, CancellationToken.None);

        outcome.Kind.Should().Be(RecommendOutcomeKind.Unavailable);
    }

    [Fact]
    public async Task RecommendAsync_DocumentNotFound_Returns404()
    {
        var docId = Guid.CreateVersion7();
        _documents.Setup(r => r.GetByIdAsync(docId, It.IsAny<CancellationToken>())).ReturnsAsync((Document?)null);

        var outcome = await CreateSut().RecommendAsync(docId, CancellationToken.None);

        outcome.Kind.Should().Be(RecommendOutcomeKind.DocumentNotFound);
        _llm.Verify(l => l.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecommendAsync_NotClassified_ReturnsNotClassified()
    {
        var docId = Guid.CreateVersion7();
        _documents.Setup(r => r.GetByIdAsync(docId, It.IsAny<CancellationToken>())).ReturnsAsync(Doc(docId));
        _classifications.Setup(r => r.GetByDocumentIdAsync(docId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DocumentClassification?)null);

        var outcome = await CreateSut().RecommendAsync(docId, CancellationToken.None);

        outcome.Kind.Should().Be(RecommendOutcomeKind.NotClassified);
        _llm.Verify(l => l.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- CreateTaskAsync ----

    [Fact]
    public async Task CreateTaskAsync_Valid_WritesRowAndAudit_InOneTransaction()
    {
        var docId = Guid.CreateVersion7();
        _documents.Setup(r => r.GetByIdAsync(docId, It.IsAny<CancellationToken>())).ReturnsAsync(Doc(docId));

        WorkflowTask? stagedTask = null;
        AuditLog? stagedAudit = null;
        _tasks.Setup(r => r.AddTrackedAsync(It.IsAny<WorkflowTask>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowTask, CancellationToken>((t, _) => stagedTask = t)
            .Returns(Task.CompletedTask);
        _audit.Setup(r => r.AddAsync(It.IsAny<AuditLog>(), It.IsAny<CancellationToken>()))
            .Callback<AuditLog, CancellationToken>((a, _) => stagedAudit = a)
            .Returns(Task.CompletedTask);

        var input = new CreateTaskInput(docId, "LegalReview", "Legal", "High", "Important contract.");
        var outcome = await CreateSut().CreateTaskAsync(input, CancellationToken.None);

        outcome.Kind.Should().Be(CreateTaskOutcomeKind.Created);
        outcome.Task!.TaskType.Should().Be("LegalReview");
        outcome.Task.Status.Should().Be(WorkflowTaskStatus.Open);
        outcome.Task.Priority.Should().Be(WorkflowPriority.High);

        // BOTH the row AND the audit were staged, inside exactly one transaction.
        stagedTask.Should().NotBeNull();
        stagedTask!.Status.Should().Be(WorkflowTaskStatus.Open);
        stagedAudit.Should().NotBeNull();
        stagedAudit!.Action.Should().Be(nameof(AuditAction.ToolSucceeded));
        _unitOfWork.Verify(u => u.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null, "Legal", "High")]      // missing taskType
    [InlineData("LegalReview", null, "High")] // missing assignedTeam
    [InlineData("LegalReview", "Legal", "NOPE")] // off-enum priority
    public async Task CreateTaskAsync_InvalidInput_RejectsWithNothingWritten(string? taskType, string? team, string priority)
    {
        var docId = Guid.CreateVersion7();
        _documents.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(Doc(docId));

        var input = new CreateTaskInput(docId, taskType, team, priority, null);
        var outcome = await CreateSut().CreateTaskAsync(input, CancellationToken.None);

        outcome.Kind.Should().Be(CreateTaskOutcomeKind.Invalid);
        _tasks.Verify(r => r.AddTrackedAsync(It.IsAny<WorkflowTask>(), It.IsAny<CancellationToken>()), Times.Never);
        _audit.Verify(r => r.AddAsync(It.IsAny<AuditLog>(), It.IsAny<CancellationToken>()), Times.Never);
        _unitOfWork.Verify(u => u.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateTaskAsync_DocumentNotFound_Returns404_NothingWritten()
    {
        var docId = Guid.CreateVersion7();
        _documents.Setup(r => r.GetByIdAsync(docId, It.IsAny<CancellationToken>())).ReturnsAsync((Document?)null);

        var input = new CreateTaskInput(docId, "LegalReview", "Legal", "High", null);
        var outcome = await CreateSut().CreateTaskAsync(input, CancellationToken.None);

        outcome.Kind.Should().Be(CreateTaskOutcomeKind.DocumentNotFound);
        _tasks.Verify(r => r.AddTrackedAsync(It.IsAny<WorkflowTask>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateTaskAsync_SoftGuardEnabled_DuplicateOpenTask_Returns409()
    {
        var docId = Guid.CreateVersion7();
        _documents.Setup(r => r.GetByIdAsync(docId, It.IsAny<CancellationToken>())).ReturnsAsync(Doc(docId));
        _tasks.Setup(r => r.CountOpenByDocumentAsync(docId, "LegalReview", It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var input = new CreateTaskInput(docId, "LegalReview", "Legal", "High", null);
        var outcome = await CreateSut(new WorkflowOptions { AllowDuplicateTasks = false })
            .CreateTaskAsync(input, CancellationToken.None);

        outcome.Kind.Should().Be(CreateTaskOutcomeKind.DuplicateOpenTask);
        _tasks.Verify(r => r.AddTrackedAsync(It.IsAny<WorkflowTask>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- ListTasksAsync ----

    [Fact]
    public async Task ListTasksAsync_PassesFiltersThrough()
    {
        var docId = Guid.CreateVersion7();
        _tasks.Setup(r => r.ListAsync(WorkflowTaskStatus.Open, docId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<WorkflowTask>
            {
                new() { Id = Guid.CreateVersion7(), DocumentId = docId, TaskType = "T", AssignedTeam = "Team", Priority = WorkflowPriority.Low, Status = WorkflowTaskStatus.Open, CreatedAt = DateTime.UtcNow },
            });

        var result = await CreateSut().ListTasksAsync(WorkflowTaskStatus.Open, docId, CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].Status.Should().Be(WorkflowTaskStatus.Open);
        _tasks.Verify(r => r.ListAsync(WorkflowTaskStatus.Open, docId, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- CompleteTaskAsync ----

    [Fact]
    public async Task CompleteTaskAsync_OpenTask_SetsCompletedAt_AndAudits()
    {
        var taskId = Guid.CreateVersion7();
        var task = new WorkflowTask
        {
            Id = taskId,
            DocumentId = Guid.CreateVersion7(),
            TaskType = "T",
            AssignedTeam = "Team",
            Priority = WorkflowPriority.Normal,
            Status = WorkflowTaskStatus.Open,
            CreatedAt = DateTime.UtcNow,
        };
        _tasks.Setup(r => r.GetByIdAsync(taskId, It.IsAny<CancellationToken>())).ReturnsAsync(task);

        var outcome = await CreateSut().CompleteTaskAsync(taskId, CancellationToken.None);

        outcome.Kind.Should().Be(CompleteTaskOutcomeKind.Completed);
        outcome.Task!.Status.Should().Be(WorkflowTaskStatus.Completed);
        outcome.Task.CompletedAt.Should().NotBeNull();
        _audit.Verify(r => r.AddAsync(It.IsAny<AuditLog>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompleteTaskAsync_AlreadyCompleted_Returns409()
    {
        var taskId = Guid.CreateVersion7();
        var task = new WorkflowTask
        {
            Id = taskId,
            DocumentId = Guid.CreateVersion7(),
            TaskType = "T",
            AssignedTeam = "Team",
            Priority = WorkflowPriority.Normal,
            Status = WorkflowTaskStatus.Completed,
            CreatedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
        };
        _tasks.Setup(r => r.GetByIdAsync(taskId, It.IsAny<CancellationToken>())).ReturnsAsync(task);

        var outcome = await CreateSut().CompleteTaskAsync(taskId, CancellationToken.None);

        outcome.Kind.Should().Be(CompleteTaskOutcomeKind.AlreadyCompleted);
        _unitOfWork.Verify(u => u.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CompleteTaskAsync_Missing_Returns404()
    {
        var taskId = Guid.CreateVersion7();
        _tasks.Setup(r => r.GetByIdAsync(taskId, It.IsAny<CancellationToken>())).ReturnsAsync((WorkflowTask?)null);

        var outcome = await CreateSut().CompleteTaskAsync(taskId, CancellationToken.None);

        outcome.Kind.Should().Be(CompleteTaskOutcomeKind.NotFound);
    }
}
