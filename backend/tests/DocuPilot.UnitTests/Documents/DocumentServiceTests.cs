using System.Text;
using DocuPilot.Models.Entities;
using DocuPilot.Models.Enums;
using DocuPilot.Repository.Abstractions;
using DocuPilot.Services.Abstractions;
using DocuPilot.Services.Documents;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;

namespace DocuPilot.UnitTests.Documents;

/// <summary>
/// Unit tests for <see cref="DocumentService"/> — the upload validation rules, the
/// entity-construction invariants (app-set UUIDv7 id, Status=Uploaded, server-side UTC
/// timestamp), and pagination normalization. Repository + file storage are mocked.
/// </summary>
public sealed class DocumentServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 5, 30, 8, 12, 0, TimeSpan.Zero);

    private readonly Mock<IDocumentRepository> _repository = new();
    private readonly Mock<IFileStorage> _fileStorage = new();
    private readonly FakeTimeProvider _timeProvider = new(FixedNow);

    private DocumentService CreateSut(long maxBytes = 25L * 1024 * 1024)
    {
        var options = Options.Create(new DocumentUploadOptions { MaxBytes = maxBytes });
        return new DocumentService(_repository.Object, _fileStorage.Object, _timeProvider, options);
    }

    private static DocumentUploadInput File(string name, string contentType, long length, string content = "data")
        => new(name, contentType, length, () => new MemoryStream(Encoding.UTF8.GetBytes(content)));

    [Fact]
    public async Task UploadAsync_ValidFile_PersistsAndReturnsUploaded()
    {
        _fileStorage
            .Setup(s => s.SaveAsync(It.IsAny<Stream>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("2026/05/30/key.pdf");

        Document? persisted = null;
        _repository
            .Setup(r => r.AddAsync(It.IsAny<Document>(), It.IsAny<CancellationToken>()))
            .Callback<Document, CancellationToken>((d, _) => persisted = d)
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        var result = await sut.UploadAsync([File("contract.pdf", "application/pdf", 1024)], CancellationToken.None);

        result.Uploaded.Should().HaveCount(1);
        result.Failed.Should().BeEmpty();

        persisted.Should().NotBeNull();
        persisted!.Status.Should().Be(DocumentStatus.Uploaded);
        persisted.UploadedAt.Should().Be(FixedNow.UtcDateTime);
        persisted.UploadedAt.Kind.Should().Be(DateTimeKind.Utc);
        persisted.FilePath.Should().Be("2026/05/30/key.pdf");
        persisted.SizeBytes.Should().Be(1024);
        persisted.Id.Should().NotBe(Guid.Empty);
        persisted.Id.Version.Should().Be(7); // app-set Guid.CreateVersion7()

        result.Uploaded[0].Id.Should().Be(persisted.Id);
        result.Uploaded[0].Status.Should().Be("Uploaded");
    }

    [Theory]
    [InlineData("note.txt", "text/plain")]
    [InlineData("report.pdf", "application/pdf")]
    [InlineData("legacy.doc", "application/msword")]
    [InlineData("memo.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    public async Task UploadAsync_AllowListedTypes_Accepted(string name, string contentType)
    {
        _fileStorage
            .Setup(s => s.SaveAsync(It.IsAny<Stream>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("key");

        var sut = CreateSut();
        var result = await sut.UploadAsync([File(name, contentType, 10)], CancellationToken.None);

        result.Uploaded.Should().HaveCount(1);
        result.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task UploadAsync_EmptyFile_Rejected()
    {
        var sut = CreateSut();
        var result = await sut.UploadAsync([File("empty.txt", "text/plain", 0)], CancellationToken.None);

        result.Uploaded.Should().BeEmpty();
        result.Failed.Should().ContainSingle().Which.Error.Should().Contain("empty");
        _fileStorage.Verify(s => s.SaveAsync(It.IsAny<Stream>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UploadAsync_OversizedFile_Rejected()
    {
        var sut = CreateSut(maxBytes: 100);
        var result = await sut.UploadAsync([File("big.pdf", "application/pdf", 101)], CancellationToken.None);

        result.Uploaded.Should().BeEmpty();
        result.Failed.Should().ContainSingle().Which.Error.Should().Contain("maximum size");
    }

    [Fact]
    public async Task UploadAsync_DisallowedExtension_Rejected()
    {
        var sut = CreateSut();
        var result = await sut.UploadAsync([File("evil.exe", "application/octet-stream", 50)], CancellationToken.None);

        result.Failed.Should().ContainSingle().Which.Error.Should().Be("Unsupported file type.");
    }

    [Fact]
    public async Task UploadAsync_ContentTypeMismatch_Rejected()
    {
        var sut = CreateSut();
        // .pdf extension but a text/plain content type → mismatch.
        var result = await sut.UploadAsync([File("fake.pdf", "text/plain", 50)], CancellationToken.None);

        result.Failed.Should().ContainSingle().Which.Error.Should().Contain("does not match");
    }

    [Fact]
    public async Task UploadAsync_MixedBatch_ReportsPerFile()
    {
        _fileStorage
            .Setup(s => s.SaveAsync(It.IsAny<Stream>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("key");

        var sut = CreateSut();
        var result = await sut.UploadAsync(
            [File("good.txt", "text/plain", 10), File("bad.exe", "application/octet-stream", 10)],
            CancellationToken.None);

        result.Uploaded.Should().HaveCount(1);
        result.Uploaded[0].FileName.Should().Be("good.txt");
        result.Failed.Should().HaveCount(1);
        result.Failed[0].FileName.Should().Be("bad.exe");
    }

    [Fact]
    public async Task UploadAsync_ContentTypeWithCharset_Accepted()
    {
        _fileStorage
            .Setup(s => s.SaveAsync(It.IsAny<Stream>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("key");

        var sut = CreateSut();
        // "text/plain; charset=utf-8" must still match the allow-list for .txt.
        var result = await sut.UploadAsync([File("note.txt", "text/plain; charset=utf-8", 10)], CancellationToken.None);

        result.Uploaded.Should().HaveCount(1);
    }

    [Theory]
    [InlineData(0, 20, 1, 20)]      // page < 1 → 1; default pageSize
    [InlineData(-5, 0, 1, 20)]      // negative page → 1; pageSize < 1 → default 20
    [InlineData(3, 500, 3, 100)]    // pageSize capped at 100
    [InlineData(2, 50, 2, 50)]      // passthrough
    public async Task ListAsync_NormalizesPaging(int page, int pageSize, int expectedPage, int expectedPageSize)
    {
        _repository
            .Setup(r => r.ListAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Array.Empty<Document>(), 0L));

        var sut = CreateSut();
        var result = await sut.ListAsync(page, pageSize, CancellationToken.None);

        result.Page.Should().Be(expectedPage);
        result.PageSize.Should().Be(expectedPageSize);
        _repository.Verify(r => r.ListAsync(expectedPage, expectedPageSize, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListAsync_MapsEntitiesAndComputesTotalPages()
    {
        var doc = new Document
        {
            Id = Guid.CreateVersion7(),
            FileName = "a.pdf",
            ContentType = "application/pdf",
            FilePath = "2026/05/30/secret-key.pdf",
            SizeBytes = 99,
            Status = DocumentStatus.Uploaded,
            UploadedAt = FixedNow.UtcDateTime,
            ProcessedAt = null,
        };
        _repository
            .Setup(r => r.ListAsync(1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<Document>)[doc], 137L));

        var sut = CreateSut();
        var result = await sut.ListAsync(1, 20, CancellationToken.None);

        result.TotalCount.Should().Be(137);
        result.TotalPages.Should().Be(7); // ceil(137/20)
        result.Items.Should().ContainSingle();
        result.Items[0].Status.Should().Be("Uploaded");
        result.Items[0].FileName.Should().Be("a.pdf");
        // DocumentListItem must not expose the internal storage key.
        typeof(DocuPilot.Models.Contracts.DocumentListItem)
            .GetProperty("FilePath").Should().BeNull();
    }

    /// <summary>Minimal fixed-time TimeProvider for deterministic UploadedAt assertions.</summary>
    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
