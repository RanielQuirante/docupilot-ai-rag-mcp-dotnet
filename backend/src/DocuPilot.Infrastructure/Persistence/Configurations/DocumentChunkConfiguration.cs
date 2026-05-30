using DocuPilot.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocuPilot.Infrastructure.Persistence.Configurations;

/// <summary>
/// Fluent EF Core mapping for <see cref="DocumentChunk"/> (table <c>DocumentChunks</c>), per the DBA
/// DA-038 spec (§P5.2 / §P5.6.2). The FIRST 1:N child of <c>Documents</c> — mapped from the MANY
/// side as <c>HasOne&lt;Document&gt;().WithMany().HasForeignKey(c =&gt; c.DocumentId)</c> (NOT
/// <c>WithOne()</c> like the 1:1 children, and NOT <c>HasMany().WithMany()</c> which would spawn a
/// join table). Uniqueness is the composite <c>UNIQUE(DocumentId, ChunkIndex)</c> (the idempotent
/// upsert key + ordered-retrieval index); FK is ON DELETE CASCADE. <c>PointId</c> is a plain
/// <c>uniqueidentifier</c> (NOT a FK, NOT unique-indexed). <c>Content</c> is <c>nvarchar(max)</c>.
/// </summary>
public sealed class DocumentChunkConfiguration : IEntityTypeConfiguration<DocumentChunk>
{
    public void Configure(EntityTypeBuilder<DocumentChunk> builder)
    {
        builder.ToTable("DocumentChunks");

        // App-set UUIDv7 PK — no DB default (DA-038 constraint #2).
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id)
            .ValueGeneratedNever();

        builder.Property(c => c.DocumentId)
            .IsRequired();

        builder.Property(c => c.ChunkIndex)
            .IsRequired();

        builder.Property(c => c.Content)
            .IsRequired()
            .HasColumnType("nvarchar(max)");

        builder.Property(c => c.TokenEstimate)
            .IsRequired();

        // PointId: deterministic Qdrant point id — NOT a FK, NOT unique-indexed (uniqueness is
        // transitive via the composite UNIQUE — DA-038 §P5.2).
        builder.Property(c => c.PointId)
            .IsRequired();

        builder.Property(c => c.CreatedAt)
            .IsRequired();

        // Composite UNIQUE index (DocumentId ASC, ChunkIndex ASC): enforces 1-chunk-per-(doc,index),
        // backs the idempotent upsert, AND backs the WHERE DocumentId=@id ORDER BY ChunkIndex
        // retrieval. One index, three jobs (DA-038 §P5.2.1). No standalone DocumentId index, no
        // PointId index.
        builder.HasIndex(c => new { c.DocumentId, c.ChunkIndex })
            .IsUnique()
            .HasDatabaseName("IX_DocumentChunks_DocumentId_ChunkIndex");

        // ONE-TO-MANY FK → Documents.Id, ON DELETE CASCADE. The key difference vs the 1:1 children:
        // .WithMany() (not .WithOne()) and a non-generic HasForeignKey lambda (DA-038 §P5.6.2).
        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(c => c.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
