using DocuPilot.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocuPilot.Infrastructure.Persistence.Configurations;

/// <summary>
/// Fluent EF Core mapping for <see cref="DocumentText"/> (table <c>DocumentTexts</c>), per the
/// DA-023 DBA spec (§P3.2 / §P3.5.2). 1:1 with <c>Documents</c>, enforced by a UNIQUE index on
/// <c>DocumentId</c> (which doubles as the idempotent-upsert key) and an ON DELETE CASCADE FK.
/// </summary>
public sealed class DocumentTextConfiguration : IEntityTypeConfiguration<DocumentText>
{
    public void Configure(EntityTypeBuilder<DocumentText> builder)
    {
        builder.ToTable("DocumentTexts");

        // App-set UUIDv7 PK — no DB default (DA-023 constraint #2).
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id)
            .ValueGeneratedNever();

        builder.Property(t => t.DocumentId)
            .IsRequired();

        builder.Property(t => t.Content)
            .IsRequired()
            .HasColumnType("nvarchar(max)");

        builder.Property(t => t.CharCount)
            .IsRequired();

        builder.Property(t => t.ExtractedAt)
            .IsRequired();

        // UNIQUE index on DocumentId: enforces the 1:1, backs the upsert-by-DocumentId
        // idempotency, and backs the WHERE DocumentId = @id detail/text lookup.
        builder.HasIndex(t => t.DocumentId)
            .IsUnique()
            .HasDatabaseName("IX_DocumentTexts_DocumentId");

        // FK → Documents.Id, ON DELETE CASCADE. FK-only (no nav on Document) keeps the
        // Document POCO lean; the relationship is expressed from DocumentText's side.
        builder.HasOne<Document>()
            .WithOne()
            .HasForeignKey<DocumentText>(t => t.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
