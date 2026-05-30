using DocuPilot.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocuPilot.Infrastructure.Persistence.Configurations;

/// <summary>
/// Fluent EF Core mapping for <see cref="AuditLog"/> (table <c>AuditLogs</c>), per the DA-023
/// DBA spec (§P3.3 / §P3.5.3) and spec §9.6. <c>EntityId</c> is a plain indexed GUID — NOT a
/// foreign key (append-only immutable log). The composite <c>(EntityId, CreatedAt DESC)</c>
/// index backs the per-document timeline and the stale-claim sweep.
/// </summary>
public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLogs");

        // App-set UUIDv7 PK — no DB default.
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id)
            .ValueGeneratedNever();

        builder.Property(a => a.EntityName)
            .IsRequired()
            .HasMaxLength(100);

        // EntityId is intentionally NOT an FK (DA-023 §P3.3) — no HasOne/HasForeignKey here.
        builder.Property(a => a.EntityId)
            .IsRequired();

        builder.Property(a => a.Action)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(a => a.DetailsJson)
            .HasColumnType("nvarchar(max)");

        builder.Property(a => a.CreatedAt)
            .IsRequired();

        // Composite index: equality on EntityId then CreatedAt DESC for newest-first timeline.
        builder.HasIndex(a => new { a.EntityId, a.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("IX_AuditLogs_EntityId_CreatedAt");
    }
}
