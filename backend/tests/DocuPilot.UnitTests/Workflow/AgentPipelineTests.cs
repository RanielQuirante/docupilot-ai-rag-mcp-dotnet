using System.Text.Json;
using DocuPilot.Models.Enums;
using DocuPilot.Services.Tools;
using DocuPilot.Services.Workflow;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DocuPilot.UnitTests.Workflow;

/// <summary>
/// Unit tests for <see cref="AgentPipeline"/> — the CONSTRAINED agent (DA-054 / ADR §2). Asserts the
/// FIXED, code-orchestrated recommend → create sequence (both via the dispatcher, both therefore
/// audited), end-to-end with a stubbed <see cref="IToolDispatcher"/>; and the fail-fast posture —
/// LLM-down at step 1 short-circuits to Unavailable and the create step NEVER runs (no half-created
/// state).
/// </summary>
public sealed class AgentPipelineTests
{
    private readonly Mock<IToolDispatcher> _dispatcher = new();

    private AgentPipeline CreateSut() => new(_dispatcher.Object, NullLogger<AgentPipeline>.Instance);

    [Fact]
    public async Task RecommendAndCreate_HappyPath_RunsRecommendThenCreate_ReturnsBoth()
    {
        var docId = Guid.CreateVersion7();
        var recommendation = new WorkflowRecommendationModel("LegalReview", "Route to legal", WorkflowPriority.High, "It is a contract.");
        var task = new WorkflowTaskModel(Guid.CreateVersion7(), docId, "LegalReview", "Legal", WorkflowPriority.High, WorkflowTaskStatus.Open, "It is a contract.", DateTime.UtcNow, null);

        _dispatcher.Setup(d => d.InvokeAsync("recommend_workflow", It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolResult.Succeeded(recommendation));
        _dispatcher.Setup(d => d.InvokeAsync("create_workflow_task", It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolResult.Succeeded(task));

        var outcome = await CreateSut().RecommendAndCreateAsync(docId, CancellationToken.None);

        outcome.Kind.Should().Be(AgentPipelineOutcomeKind.Succeeded);
        outcome.Recommendation!.RecommendedWorkflow.Should().Be("LegalReview");
        outcome.Task!.TaskType.Should().Be("LegalReview");

        // FIXED sequence: recommend then create, each dispatched (so each audited by the dispatcher).
        _dispatcher.Verify(d => d.InvokeAsync("recommend_workflow", It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()), Times.Once);
        _dispatcher.Verify(d => d.InvokeAsync("create_workflow_task", It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RecommendAndCreate_LlmDownAtStep1_FailsFast_CreateNeverRuns()
    {
        var docId = Guid.CreateVersion7();
        _dispatcher.Setup(d => d.InvokeAsync("recommend_workflow", It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolResult.Unavailable("llm down"));

        var outcome = await CreateSut().RecommendAndCreateAsync(docId, CancellationToken.None);

        outcome.Kind.Should().Be(AgentPipelineOutcomeKind.Unavailable);
        _dispatcher.Verify(d => d.InvokeAsync("create_workflow_task", It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecommendAndCreate_DocumentNotFoundAtRecommend_Returns404_NoCreate()
    {
        var docId = Guid.CreateVersion7();
        _dispatcher.Setup(d => d.InvokeAsync("recommend_workflow", It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolResult.NotFound("missing"));

        var outcome = await CreateSut().RecommendAndCreateAsync(docId, CancellationToken.None);

        outcome.Kind.Should().Be(AgentPipelineOutcomeKind.DocumentNotFound);
        _dispatcher.Verify(d => d.InvokeAsync("create_workflow_task", It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
