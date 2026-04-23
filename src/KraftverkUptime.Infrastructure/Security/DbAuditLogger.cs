using System.Text.Json;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Infrastructure.Persistence.Entities;

namespace KraftverkUptime.Infrastructure.Security;

public sealed class DbAuditLogger : IAuditLogger
{
    private readonly KraftverkDbContext _db;
    private readonly ICurrentUser _user;

    public DbAuditLogger(KraftverkDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task LogAsync(string action, string entityType, string entityId, object? payload, CancellationToken ct = default)
    {
        var entry = new AuditLogEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            UserId = _user.UserId,
            OwnerOrgId = _user.OrgId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            CorrelationId = System.Diagnostics.Activity.Current?.TraceId.ToString(),
            PayloadJson = payload is null ? null : JsonSerializer.Serialize(payload)
        };

        _db.AuditLog.Add(entry);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
