using DocuPilot.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocuPilot.Infrastructure.Persistence.Configurations;

/// <summary>
/// Fluent EF Core mapping for <see cref="WorkflowTask"/> (table <c>WorkflowTasks</c>), per the DBA
/// DA-053 spec (§P8.2 / §P8.5.2). The SECOND 1:N child of <c>Documents</c> — mapped from the MANY
/// side as <c>HasOne&lt;Document&gt;().WithMany().HasForeignKey(t =&gt; t.DocumentId)</c> (NOT
/// <c>WithOne()</c> like the 1:1 children, and NOT <c>HasMany().WithMany()</c> which would spawn a
/// join table). The KEY difference vs <see cref="DocumentChunkConfiguration"/>: there is NO unique
/// constraint — a document may hold many tasks (PM Q7), so <c>IX_WorkflowTasks_DocumentId</c> is a
/// plain non-unique index (NOT <c>.IsUnique()</c>). <c>Priority</c>/<c>Status</c> are enum-strings;
/// <c>Reason</c> is <c>nvarchar(max)</c> nullable. FK is ON DELETE CASCADE.
/// </summary>
public sealed class WorkflowTaskConfiguration : IEntityTypeConfiguration<WorkflowTask>
{
    public void Configure(EntityTypeBuilder<WorkflowTask> builder)
    {
        builder.ToTable("WorkflowTasks");

        // App-set UUIDv7 PK — no DB default (DA-053 constraint #2).
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id)
            .ValueGeneratedNever();

        builder.Property(t => t.DocumentId)
            .IsRequired();

        builder.Property(t => t.TaskType)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(t => t.AssignedTeam)
            .IsRequired()
            .HasMaxLength(100);

        // Enum-string (NVARCHAR(50)) — the closed set is app-enforced; the converter only ever
        // writes a valid member name (DA-053 §P8.5.2).
        builder.Property(t => t.Priority)
            .IsRequired()
            .HasMaxLength(50)
            .HasConversion<string>();

        builder.Property(t => t.Reason)
            .HasColumnType("nvarchar(max)");

        builder.Property(t => t.Status)
            .IsRequired()
            .HasMaxLength(50)
            .HasConversion<string>();

        builder.Property(t => t.CreatedAt)
            .IsRequired();

        builder.Property(t => t.CompletedAt);

        // NON-unique index on DocumentId: backs the cascade FK + the "tasks for this document" read.
        // NOT .IsUnique() (1:N — many tasks per doc, DA-053 §P8.2.1 / constraint #4).
        builder.HasIndex(t => t.DocumentId)
            .HasDatabaseName("IX_WorkflowTasks_DocumentId");

        // NON-unique index on Status: backs the §11.7 list-page Open/Completed filter.
        builder.HasIndex(t => t.Status)
            .HasDatabaseName("IX_WorkflowTasks_Status");

        // ONE-TO-MANY FK → Documents.Id, ON DELETE CASCADE. .WithMany() (not .WithOne()) and a
        // non-generic HasForeignKey lambda — the DocumentChunks 1:N form (DA-053 §P8.5.2).
        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(t => t.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
