using DocuPilot.Services.Abstractions;
using DocuPilot.Services.Documents;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace DocuPilot.UnitTests.Documents;

/// <summary>
/// Exhaustive deterministic unit tests for <see cref="RecursiveCharacterChunker"/> (ADR §1/§9). No
/// network, no LLM — pure CPU string logic, so behavior is fully reproducible. Covers: single short
/// chunk (no overlap), size cap, gap-free 0-based indexing, overlap content, boundary-aware split,
/// max-chunks cap, empty/whitespace, token estimate, and determinism.
/// </summary>
public sealed class RecursiveCharacterChunkerTests
{
    private static RecursiveCharacterChunker Sut(int maxChars = 4000, int overlapChars = 600, int maxChunks = 1000)
        => new(Options.Create(new ChunkingConfig
        {
            MaxChars = maxChars,
            OverlapChars = overlapChars,
            MaxChunksPerDocument = maxChunks,
        }));

    [Fact]
    public void Chunk_ShortDocument_ProducesSingleChunkIndexZeroNoOverlap()
    {
        var result = Sut().Chunk("This is a short document.");

        result.Should().HaveCount(1);
        result[0].ChunkIndex.Should().Be(0);
        result[0].Content.Should().Be("This is a short document.");
        result[0].CharCount.Should().Be("This is a short document.".Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n\t  \n")]
    public void Chunk_EmptyOrWhitespace_ProducesNoChunks(string content)
    {
        Sut().Chunk(content).Should().BeEmpty();
    }

    [Fact]
    public void Chunk_TokenEstimate_IsCeilOfCharsOverFour()
    {
        var result = Sut().Chunk("abcde"); // 5 chars → ceil(5/4) = 2

        result.Should().HaveCount(1);
        result[0].TokenEstimate.Should().Be(2);
    }

    [Fact]
    public void Chunk_ExceedsBudget_SplitsIntoMultipleChunksEachWithinCap()
    {
        // 10 sentences of ~20 chars each; small budget forces several chunks.
        var sentences = string.Join(" ", Enumerable.Range(0, 10).Select(i => $"Sentence number {i:00}."));
        var result = Sut(maxChars: 60, overlapChars: 0).Chunk(sentences);

        result.Should().HaveCountGreaterThan(1);
        result.Should().OnlyContain(c => c.CharCount <= 60);
    }

    [Fact]
    public void Chunk_ChunkIndices_AreZeroBasedAndGapFree()
    {
        var sentences = string.Join(" ", Enumerable.Range(0, 30).Select(i => $"Sentence number {i:00}."));
        var result = Sut(maxChars: 50, overlapChars: 10).Chunk(sentences);

        result.Should().HaveCountGreaterThan(1);
        for (var i = 0; i < result.Count; i++)
        {
            result[i].ChunkIndex.Should().Be(i);
        }
    }

    [Fact]
    public void Chunk_WithOverlap_SecondChunkBeginsWithTailOfFirstBody()
    {
        // No separators → forces a hard cut into fixed-size pieces, so overlap is exact and checkable.
        // MaxChars=40 is the TOTAL chunk size including overlap; with overlap=10 the body budget is
        // 30, so bodies are 30-char runs and chunk 1 = overlap(10) + body(30) = exactly MaxChars.
        var content = new string('a', 30) + new string('b', 30); // 60 chars, no separators
        var result = Sut(maxChars: 40, overlapChars: 10).Chunk(content);

        result.Should().HaveCountGreaterOrEqualTo(2);
        // First chunk = first 30 'a' (no overlap on the first chunk, sized to the body budget).
        result[0].Content.Should().Be(new string('a', 30));
        // Second chunk re-includes the last 10 chars of the first body, then its own 30-char body.
        result[1].Content[..10].Should().Be(new string('a', 10)); // overlap tail of chunk 0
        result[1].Content[10..].Should().Be(new string('b', 30));  // then chunk 1's own body
        result[1].CharCount.Should().Be(40); // == MaxChars (overlap included)
    }

    [Fact]
    public void Chunk_FirstChunk_NeverHasOverlap()
    {
        var content = new string('x', 200);
        // MaxChars=40, overlap=15 → body budget 25. The first chunk is one body with NO prepended
        // overlap; it equals the body budget, not MaxChars.
        var result = Sut(maxChars: 40, overlapChars: 15).Chunk(content);

        result[0].Content.Should().Be(new string('x', 25)); // body budget, no prepended overlap
        result[0].CharCount.Should().Be(25);
    }

    [Fact]
    public void Chunk_BoundaryAware_PrefersParagraphThenSentenceSplits()
    {
        // Two paragraphs; budget fits one paragraph but not both → split on the paragraph break.
        var p1 = "First paragraph with several words here.";
        var p2 = "Second paragraph also with several words.";
        var content = p1 + "\n\n" + p2;

        var result = Sut(maxChars: 50, overlapChars: 0).Chunk(content);

        result.Should().HaveCount(2);
        result[0].Content.Should().Contain("First paragraph");
        result[1].Content.Should().Contain("Second paragraph");
    }

    [Fact]
    public void Chunk_SeparatorFreeRunLongerThanBudget_HardCuts()
    {
        var content = new string('z', 130); // single run, no separators, > budget
        var result = Sut(maxChars: 50, overlapChars: 0).Chunk(content);

        result.Should().HaveCount(3); // 50 + 50 + 30
        result.Sum(c => c.CharCount).Should().Be(130);
        result.Should().OnlyContain(c => c.CharCount <= 50);
    }

    [Fact]
    public void Chunk_MaxChunksCap_LimitsOutputCount()
    {
        var sentences = string.Join(" ", Enumerable.Range(0, 100).Select(i => $"Sentence number {i:000}."));
        var result = Sut(maxChars: 30, overlapChars: 0, maxChunks: 5).Chunk(sentences);

        result.Should().HaveCount(5); // capped, even though the text would yield far more
        result[^1].ChunkIndex.Should().Be(4); // still gap-free up to the cap
    }

    [Fact]
    public void Chunk_OptionsOverride_TakesPrecedenceOverConfigDefaults()
    {
        var content = new string('q', 100);
        var result = Sut(maxChars: 4000).Chunk(content, new ChunkingOptions(MaxChars: 25, OverlapChars: 0, MaxChunksPerDocument: 1000));

        result.Should().HaveCount(4); // 100 / 25
        result.Should().OnlyContain(c => c.CharCount <= 25);
    }

    [Fact]
    public void Chunk_IsDeterministic_SameInputYieldsIdenticalChunks()
    {
        var content = string.Join(" ", Enumerable.Range(0, 40).Select(i => $"Word{i}"));

        var a = Sut(maxChars: 40, overlapChars: 8).Chunk(content);
        var b = Sut(maxChars: 40, overlapChars: 8).Chunk(content);

        a.Select(c => c.Content).Should().Equal(b.Select(c => c.Content));
    }
}
