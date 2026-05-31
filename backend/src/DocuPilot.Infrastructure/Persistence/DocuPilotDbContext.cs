using DocuPilot.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace DocuPilot.Infrastructure.Persistence;

/// <summary>
/// The application's EF Core <see cref="DbContext"/>. Lives in Infrastructure
/// (DA-011 §2.7 — "the DbContext lives HERE, not in Repository"). Entity mappings
/// are applied from <see cref="IEntityTypeConfiguration{TEntity}"/> implementations in
/// the <c>Persistence/Configurations</c> folder.
/// </summary>
public sealed class DocuPilotDbContext : DbContext
{
    public DocuPilotDbContext(DbContextOptions<DocuPilotDbContext> options)
        : base(options)
    {
    }

    public DbSet<Document> Documents => Set<Document>();

    public DbSet<DocumentText> DocumentTexts => Set<DocumentText>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<DocumentClassification> DocumentClassifications => Set<DocumentClassification>();

    public DbSet<ExtractedMetadata> ExtractedMetadata => Set<ExtractedMetadata>();

    public DbSet<DocumentChunk> DocumentChunks => Set<DocumentChunk>();

    public DbSet<WorkflowTask> WorkflowTasks => Set<WorkflowTask>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DocuPilotDbContext).Assembly);
    }
}
