using DeployOrchestrator.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace DeployOrchestrator.Api.Services;

/// <summary>
/// Complete audit trail persisted to Azure SQL ("Complete Audit & History" benefit).
/// </summary>
public interface IAuditService
{
    Task LogAsync(Guid requestId, string action, string detail, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEntry>> GetAsync(Guid requestId, CancellationToken ct = default);
}

public record AuditEntry(Guid RequestId, string Action, string Detail, string Actor, DateTimeOffset TimestampUtc);

public class AuditService : IAuditService
{
    private readonly Data.OrchestratorDbContext _db;

    public AuditService(Data.OrchestratorDbContext db) => _db = db;

    public async Task LogAsync(Guid requestId, string action, string detail, CancellationToken ct = default)
    {
        _db.AuditEntries.Add(new Data.AuditEntryRow
        {
            Id = Guid.NewGuid(),
            RequestId = requestId,
            Action = action,
            Detail = detail,
            TimestampUtc = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AuditEntry>> GetAsync(Guid requestId, CancellationToken ct = default)
    {
        var rows = await _db.AuditEntries
            .Where(a => a.RequestId == requestId)
            .OrderBy(a => a.TimestampUtc)
            .ToListAsync(ct);
        return rows.Select(r => new AuditEntry(r.RequestId, r.Action, r.Detail, "orchestrator", r.TimestampUtc)).ToList();
    }
}
