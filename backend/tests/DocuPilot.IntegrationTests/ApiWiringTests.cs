using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DocuPilot.Models.Contracts;
using DocuPilot.Models.Entities;
using DocuPilot.Models.Enums;
using DocuPilot.Services.Abstractions;
using DocuPilot.Services.Common;
using FluentAssertions;

namespace DocuPilot.IntegrationTests;

/// <summary>
/// DA-062 HTTP-level wiring tests. Each test drives a real HTTP request through the full API pipeline
/// (controller → service → repository → EF/SQLite) via the <see cref="DocuPilotApiFactory"/>, with the
/// external ports stubbed. The goal is high-signal coverage of the API ↔ EF ↔ repo wiring + the frozen
/// contracts — including the new Phase-9 endpoints and the DA-059 404 fix — NOT exhaustive behavior.
/// </summary>
public sealed class ApiWiringTests : IClassFixture<DocuPilotApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly DocuPilotApiFactory _factory;

    public ApiWiringTests(DocuPilotApiFactory factory) => _factory = factory;

    // ---- Phase 1: health ----

    [Fact]
    public async Task Health_returns_200_healthy()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        body.GetProperty("status").GetString().Should().Be("healthy");
        body.GetProperty("service").GetString().Should().Be("DocuPilot.Api");
    }

    // ---- Phase 2: upload + list ----

    [Fact]
    public async Task Upload_txt_then_list_shows_the_document()
    {
        var client = _factory.CreateClient();
        var fileName = $"wiring-{Guid.NewGuid():N}.txt";

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent("Hello DocuPilot integration test."u8.ToArray());
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(fileContent, "files", fileName);

        var uploadResponse = await client.PostAsync("/api/documents/upload", content);

        uploadResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var uploaded = await uploadResponse.Content.ReadFromJsonAsync<UploadDocumentResponse>(Json);
        uploaded!.Uploaded.Should().ContainSingle();
        var documentId = uploaded.Uploaded[0].Id;

        // The doc must appear in the paged library list.
        var listResponse = await client.GetAsync("/api/documents?page=1&pageSize=100");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await listResponse.Content.ReadFromJsonAsync<PagedResult<DocumentListItem>>(Json);
        page!.Items.Should().Contain(d => d.Id == documentId && d.FileName == fileName);
    }

    [Fact]
    public async Task Upload_with_no_files_returns_400()
    {
        var client = _factory.CreateClient();
        using var content = new MultipartFormDataContent(); // no "files" part

        var response = await client.PostAsync("/api/documents/upload", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- Phase 3: detail + audit ----

    [Fact]
    public async Task Detail_and_audit_return_200_for_an_uploaded_document()
    {
        var client = _factory.CreateClient();
        var documentId = await UploadOneAsync(client);

        var detailResponse = await client.GetAsync($"/api/documents/{documentId}");
        detailResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = await detailResponse.Content.ReadFromJsonAsync<DocumentDetail>(Json);
        detail!.Id.Should().Be(documentId);

        // Audit timeline is always 200 (empty array allowed). Upload writes a Queued event.
        var auditResponse = await client.GetAsync($"/api/documents/{documentId}/audit");
        auditResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var audit = await auditResponse.Content.ReadFromJsonAsync<List<AuditLogEntry>>(Json);
        audit.Should().NotBeNull();
    }

    [Fact]
    public async Task Detail_for_unknown_id_returns_404()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/documents/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- Phase 9: dashboard stats ----

    [Fact]
    public async Task Dashboard_stats_reflect_seeded_documents()
    {
        var client = _factory.CreateClient();

        // Baseline.
        var before = await GetStatsAsync(client);

        // Seed a ReadyForSearch + a Failed document directly (counts must move).
        var ready = NewDocument(DocumentStatus.ReadyForSearch);
        var failed = NewDocument(DocumentStatus.Failed);
        await using (var db = _factory.CreateDbContext())
        {
            db.Documents.AddRange(ready, failed);
            db.DocumentClassifications.Add(new DocumentClassification
            {
                Id = Guid.NewGuid(),
                DocumentId = ready.Id,
                Classification = DocumentCategory.Contract,
                Confidence = 0.9m,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var after = await GetStatsAsync(client);

        after.TotalDocuments.Should().Be(before.TotalDocuments + 2);
        after.ReadyForSearch.Should().Be(before.ReadyForSearch + 1);
        after.Failed.Should().Be(before.Failed + 1);
        after.ClassificationBreakdown.Should().Contain(c => c.Category == "Contract");
    }

    // ---- Phase 9: audit-logs ----

    [Fact]
    public async Task AuditLogs_returns_paged_result()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/audit-logs?page=1&pageSize=50");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<PagedResult<AuditLogListItem>>(Json);
        page.Should().NotBeNull();
        page!.Page.Should().Be(1);
    }

    [Fact]
    public async Task AuditLogs_with_invalid_action_returns_400()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/audit-logs?action=NotARealAction");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- Phase 6: search (stubbed embedder + vector store → deterministic hit) ----

    [Fact]
    public async Task Search_returns_a_hydrated_hit_from_the_stubbed_vector_store()
    {
        var client = _factory.CreateClient();

        // Seed a ReadyForSearch document with one chunk + a classification, then script the vector store
        // to return a hit pointing at that chunk. This proves embed → search → hydrate → map wiring.
        var doc = NewDocument(DocumentStatus.ReadyForSearch, "searchable.txt");
        var chunk = new DocumentChunk
        {
            Id = Guid.NewGuid(),
            DocumentId = doc.Id,
            ChunkIndex = 0,
            Content = "The quarterly revenue grew by 12 percent.",
            TokenEstimate = 10,
            PointId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
        };
        await using (var db = _factory.CreateDbContext())
        {
            db.Documents.Add(doc);
            db.DocumentChunks.Add(chunk);
            db.DocumentClassifications.Add(new DocumentClassification
            {
                Id = Guid.NewGuid(),
                DocumentId = doc.Id,
                Classification = DocumentCategory.LegalDocument,
                Confidence = 0.8m,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        _factory.VectorStore.Hits.Clear();
        _factory.VectorStore.Hits.Add(new ChunkHit(
            chunk.PointId, doc.Id, chunk.Id, chunk.ChunkIndex, Score: 0.95f, Snippet: "revenue grew"));

        var response = await client.PostAsJsonAsync("/api/search", new SearchRequest("revenue growth"), Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<SearchResponse>(Json);
        result!.Results.Should().Contain(r =>
            r.DocumentId == doc.Id
            && r.MatchedText == chunk.Content
            && r.Classification == "Legal Document");

        _factory.VectorStore.Hits.Clear();
    }

    // ---- Phase 7: ask (empty retrieval short-circuits to a grounded not-found, NO LLM call) ----

    [Fact]
    public async Task Ask_with_no_retrieval_returns_200_not_found_answer()
    {
        var client = _factory.CreateClient();
        _factory.VectorStore.Hits.Clear(); // no hits ⇒ grounding short-circuit, answerFound=false

        var response = await client.PostAsJsonAsync("/api/ask", new AskRequest("What is the meaning of life?"), Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var answer = await response.Content.ReadFromJsonAsync<AskResponse>(Json);
        answer!.AnswerFound.Should().BeFalse();
        answer.Citations.Should().BeEmpty();
    }

    // ---- Phase 8: tools + workflow-tasks (incl. the DA-059 404 fix) ----

    [Fact]
    public async Task Tools_list_returns_200_with_definitions()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/tools");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var tools = await response.Content.ReadFromJsonAsync<List<ToolDefinitionDto>>(Json);
        tools.Should().NotBeNullOrEmpty();
        tools!.Should().Contain(t => t.Name == "create_workflow_task");
    }

    [Fact]
    public async Task WorkflowTask_create_then_list_then_complete()
    {
        var client = _factory.CreateClient();
        var documentId = await UploadOneAsync(client);

        // Create (routes through the audited create_workflow_task tool) → 201.
        var createRequest = new CreateWorkflowTaskRequest(documentId, "Manual Review", "Operations", "Normal", "Needs a look.");
        var createResponse = await client.PostAsJsonAsync("/api/workflow-tasks", createRequest, Json);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<WorkflowTaskDto>(Json);
        created!.Status.Should().Be("Open");

        // List filtered by document → contains it.
        var listResponse = await client.GetAsync($"/api/workflow-tasks?documentId={documentId}");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var tasks = await listResponse.Content.ReadFromJsonAsync<List<WorkflowTaskDto>>(Json);
        tasks!.Should().Contain(t => t.Id == created.Id);

        // Complete → 200, Status=Completed.
        var completeResponse = await client.PostAsync($"/api/workflow-tasks/{created.Id}/complete", content: null);
        completeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var completed = await completeResponse.Content.ReadFromJsonAsync<WorkflowTaskDto>(Json);
        completed!.Status.Should().Be("Completed");
        completed.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task WorkflowTask_create_for_nonexistent_document_returns_404()
    {
        // DA-059 (DA-057-D1) end-to-end: a syntactically valid but non-existent documentId must flow
        // through the dispatcher and come back as 404 (NOT 400), consistent with any other missing doc.
        var client = _factory.CreateClient();
        var request = new CreateWorkflowTaskRequest(Guid.NewGuid(), "Manual Review", "Operations", "Normal", null);

        var response = await client.PostAsJsonAsync("/api/workflow-tasks", request, Json);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- Phase 8: recommend (stubbed LLM → 200 recommendation shape) ----

    [Fact]
    public async Task Recommend_returns_200_recommendation_for_a_classified_document()
    {
        var client = _factory.CreateClient();

        // Recommend requires the document to be classified (else 409). Seed a Classified doc + classification.
        var doc = NewDocument(DocumentStatus.Classified, "recommendable.txt");
        await using (var db = _factory.CreateDbContext())
        {
            db.Documents.Add(doc);
            db.DocumentClassifications.Add(new DocumentClassification
            {
                Id = Guid.NewGuid(),
                DocumentId = doc.Id,
                Classification = DocumentCategory.Invoice,
                Confidence = 0.85m,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var response = await client.PostAsJsonAsync("/api/workflows/recommend", new RecommendWorkflowRequest(doc.Id), Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var recommendation = await response.Content.ReadFromJsonAsync<WorkflowRecommendationResponse>(Json);
        recommendation!.RecommendedWorkflow.Should().Be("Manual Review");
        recommendation.Priority.Should().Be("Normal");
    }

    // ---- helpers ----

    private async Task<Guid> UploadOneAsync(HttpClient client)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent("seed content"u8.ToArray());
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(fileContent, "files", $"seed-{Guid.NewGuid():N}.txt");

        var response = await client.PostAsync("/api/documents/upload", content);
        response.EnsureSuccessStatusCode();
        var uploaded = await response.Content.ReadFromJsonAsync<UploadDocumentResponse>(Json);
        return uploaded!.Uploaded[0].Id;
    }

    private async Task<DashboardStats> GetStatsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/dashboard/stats");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<DashboardStats>(Json))!;
    }

    private static Document NewDocument(DocumentStatus status, string fileName = "seed.txt") => new()
    {
        Id = Guid.NewGuid(),
        FileName = fileName,
        ContentType = "text/plain",
        FilePath = $"test/{Guid.NewGuid():N}.txt",
        SizeBytes = 12,
        Status = status,
        UploadedAt = DateTime.UtcNow,
    };
}
