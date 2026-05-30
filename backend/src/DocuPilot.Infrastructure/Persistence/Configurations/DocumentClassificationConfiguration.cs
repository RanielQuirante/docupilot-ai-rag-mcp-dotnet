using DocuPilot.Models.Entities;
using DocuPilot.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DocuPilot.Infrastructure.Persistence.Configurations;

/// <summary>
/// Fluent EF Core mapping for <see cref="DocumentClassification"/> (table
/// <c>DocumentClassifications</c>), per the DBA DA-031 spec (§P4.2 / §P4.6.2). 1:1 with
/// <c>Documents</c> via a UNIQUE index on <c>DocumentId</c> (also the idempotent-upsert key) and
/// an ON DELETE CASCADE FK. The <see cref="DocumentCategory"/> enum persists as its spec
/// <b>display string</b> (e.g. "Employee Record") via a <see cref="ValueConverter{TModel,TProvider}"/>
/// over the single map in <see cref="DocumentCategoryNames"/> (§P4.5, recommended option 1).
/// </summary>
public sealed class DocumentClassificationConfiguration : IEntityTypeConfiguration<DocumentClassification>
{
    public void Configure(EntityTypeBuilder<DocumentClassification> builder)
    {
        builder.ToTable("DocumentClassifications");

        // App-set UUIDv7 PK — no DB default (DA-031 constraint #2).
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id)
            .ValueGeneratedNever();

        builder.Property(c => c.DocumentId)
            .IsRequired();

        // Persist the category as its spec display string (with spaces) so the column equals the
        // spec/API/prompt category text with no re-mapping in the read path (DA-031 §P4.5).
        var categoryConverter = new ValueConverter<DocumentCategory, string>(
            category => DocumentCategoryNames.ToDisplay(category),
            value => DocumentCategoryNames.Coerce(value));

        builder.Property(c => c.Classification)
            .IsRequired()
            .HasMaxLength(100)
            .HasConversion(categoryConverter);

        builder.Property(c => c.Confidence)
            .IsRequired()
            .HasPrecision(5, 4);

        builder.Property(c => c.Reason)
            .HasColumnType("nvarchar(max)");

        builder.Property(c => c.Model)
            .HasMaxLength(100);

        builder.Property(c => c.CreatedAt)
            .IsRequired();

        // UNIQUE index on DocumentId: enforces the 1:1, backs upsert-by-DocumentId idempotency,
        // and backs the WHERE DocumentId = @id detail/classification lookup.
        builder.HasIndex(c => c.DocumentId)
            .IsUnique()
            .HasDatabaseName("IX_DocumentClassifications_DocumentId");

        // FK → Documents.Id, ON DELETE CASCADE. FK-only (no nav on Document) keeps the POCO lean.
        builder.HasOne<Document>()
            .WithOne()
            .HasForeignKey<DocumentClassification>(c => c.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
