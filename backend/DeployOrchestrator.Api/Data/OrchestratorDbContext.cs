using DeployOrchestrator.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace DeployOrchestrator.Api.Data;

public class OrchestratorDbContext : DbContext
{
    public OrchestratorDbContext(DbContextOptions<OrchestratorDbContext> options) : base(options) { }

    public DbSet<DeploymentRequest> DeploymentRequests => Set<DeploymentRequest>();
    public DbSet<AuditEntryRow> AuditEntries => Set<AuditEntryRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeploymentRequest>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.RequestedBy).IsRequired().HasMaxLength(200);
            b.Property(x => x.Subscription).HasMaxLength(200);
            b.Property(x => x.State).HasMaxLength(100);
            b.Property(x => x.ResourceName).HasMaxLength(200);
            b.Property(x => x.ValidationError).HasMaxLength(1000);
            b.Property(x => x.StatusMessage).HasMaxLength(1000);
            b.Property(x => x.TerraformScriptFolder).HasMaxLength(300);
            b.Property(x => x.PipelineId).HasMaxLength(100);
            // Field bag + missing fields persisted as JSON (works on both Azure SQL and InMemory).
            b.Property(x => x.Fields).HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new());
            b.Property(x => x.MissingFields).HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => System.Text.Json.JsonSerializer.Deserialize<List<string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new());
        });

        modelBuilder.Entity<AuditEntryRow>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Action).HasMaxLength(100);
            b.Property(x => x.Detail).HasMaxLength(2000);
        });
    }
}

public class AuditEntryRow
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; set; }
}
