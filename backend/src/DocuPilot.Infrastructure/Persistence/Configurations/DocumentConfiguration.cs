using DocuPilot.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocuPilot.Infrastructure.Persistence.Configurations;

/// <summary>
/// Fluent EF Core mapping for <see cref="Document"/> (table <c>Documents</c>), per the
/// DA-015 DBA spec. Keeps the entity a clean POCO (no attributes). Column types/lengths,
/// the single <c>UploadedAt DESC</c> index, and the enum-to-string conversion all live here.
/// </summary>
public sealed class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> builder)
    {
        builder.ToTable("Documents");

        // Clustered PK by default. Id is app-set via Guid.CreateVersion7() in the service —
        // NO ValueGeneratedOnAdd / HasDefaultValueSql (DA-015 §5, constraint #2).
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id)
            .ValueGeneratedNever();

        builder.Property(d => d.FileName)
            .IsRequired()
            .HasMaxLength(255);

        builder.Property(d => d.ContentType)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(d => d.FilePath)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(d => d.SizeBytes)
            .IsRequired();

        // Persist the enum as its string name (e.g. "Uploaded") — human-readable and
        // resilient to enum reordering (DA-015 §4.3).
        builder.Property(d => d.Status)
            .IsRequired()
            .HasMaxLength(50)
            .HasConversion<string>();

        builder.Property(d => d.UploadedAt)
            .IsRequired();

        builder.Property(d => d.ProcessedAt);

        // The ONLY non-clustered index in Phase 2: backs the newest-first paged list
        // query. DESC matches the ORDER BY so the engine can seek the top page.
        // NO index on Status (deferred to Phase 3) — DA-015 §3.1, constraint #6.
        builder.HasIndex(d => d.UploadedAt)
            .IsDescending()
            .HasDatabaseName("IX_Documents_UploadedAt");
    }
}
