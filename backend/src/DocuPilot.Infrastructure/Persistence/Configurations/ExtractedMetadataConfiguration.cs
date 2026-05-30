using DocuPilot.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocuPilot.Infrastructure.Persistence.Configurations;

/// <summary>
/// Fluent EF Core mapping for <see cref="ExtractedMetadata"/> (table <c>ExtractedMetadata</c>),
/// per the DBA DA-031 spec (§P4.3 / §P4.6.3). 1:1 with <c>Documents</c> via a UNIQUE index on
/// <c>DocumentId</c> (also the idempotent-upsert key) and an ON DELETE CASCADE FK. Metadata is
/// stored schemaless as <c>MetadataJson NVARCHAR(MAX)</c> NOT NULL (always at minimum <c>"{}"</c>).
/// </summary>
public sealed class ExtractedMetadataConfiguration : IEntityTypeConfiguration<ExtractedMetadata>
{
    public void Configure(EntityTypeBuilder<ExtractedMetadata> builder)
    {
        builder.ToTable("ExtractedMetadata");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id)
            .ValueGeneratedNever();

        builder.Property(m => m.DocumentId)
            .IsRequired();

        builder.Property(m => m.MetadataJson)
            .IsRequired()
            .HasColumnType("nvarchar(max)");

        builder.Property(m => m.Model)
            .HasMaxLength(100);

        builder.Property(m => m.CreatedAt)
            .IsRequired();

        builder.HasIndex(m => m.DocumentId)
            .IsUnique()
            .HasDatabaseName("IX_ExtractedMetadata_DocumentId");

        builder.HasOne<Document>()
            .WithOne()
            .HasForeignKey<ExtractedMetadata>(m => m.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
