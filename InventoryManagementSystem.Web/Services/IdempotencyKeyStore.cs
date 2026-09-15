using InventoryManagementSystem.Core.Entities;
using InventoryManagementSystem.Core.Interfaces;
using InventoryManagementSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace InventoryManagementSystem.Web.Services;

/// <summary>PostgreSQL-backed idempotency coordinator shared by API instances.</summary>
public sealed class IdempotencyKeyStore(
    InventoryDbContext context,
    ITenantContext tenantContext) : IIdempotencyKeyStore
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(2);

    public async Task ExecuteAsync(string scope, string key, string requestHash, Func<Task> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);
        ArgumentNullException.ThrowIfNull(operation);
        if (!tenantContext.IsResolved)
        {
            throw new InvalidOperationException("A tenant context is required for idempotency.");
        }

        var record = await ClaimAsync(scope, key, requestHash);
        if (record is null)
        {
            return;
        }

        try
        {
            await operation();
            record.Status = IdempotencyRecordStatus.Completed;
            record.ResponseStatusCode = StatusCodes.Status204NoContent;
            record.CompletedAt = DateTimeOffset.UtcNow;
            record.LeaseUntil = null;
            record.LastError = null;
            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            record.Status = IdempotencyRecordStatus.Failed;
            record.LeaseUntil = null;
            record.LastError = ex.Message[..Math.Min(ex.Message.Length, 4096)];
            await context.SaveChangesAsync();
            throw;
        }
    }

    private async Task<IdempotencyRecord?> ClaimAsync(string scope, string key, string requestHash)
    {
        var now = DateTimeOffset.UtcNow;
        var expired = await context.IdempotencyRecords
            .Where(record => record.ExpiresAt <= now)
            .Take(100)
            .ToListAsync();
        if (expired.Count > 0)
        {
            context.IdempotencyRecords.RemoveRange(expired);
            await context.SaveChangesAsync();
        }

        while (true)
        {
            var record = await context.IdempotencyRecords
                .SingleOrDefaultAsync(item => item.Scope == scope && item.Key == key);
            if (record is not null)
            {
                if (!string.Equals(record.RequestHash, requestHash, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The idempotency key was already used with a different request.");
                }

                if (record.Status == IdempotencyRecordStatus.Completed)
                {
                    return null;
                }

                if (record.Status == IdempotencyRecordStatus.InProgress && record.LeaseUntil > now)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100));
                    context.Entry(record).State = EntityState.Detached;
                    now = DateTimeOffset.UtcNow;
                    continue;
                }

                record.Status = IdempotencyRecordStatus.InProgress;
                record.AttemptCount++;
                record.LeaseUntil = now.Add(ClaimLease);
                record.LastError = null;
                await context.SaveChangesAsync();
                return record;
            }

            record = new IdempotencyRecord
            {
                TenantId = tenantContext.TenantId,
                Scope = scope,
                Key = key,
                RequestHash = requestHash,
                AttemptCount = 1,
                CreatedAt = now,
                ExpiresAt = now.Add(Retention),
                LeaseUntil = now.Add(ClaimLease)
            };
            context.IdempotencyRecords.Add(record);
            try
            {
                await context.SaveChangesAsync();
                return record;
            }
            catch (DbUpdateException)
            {
                context.ChangeTracker.Clear();
                now = DateTimeOffset.UtcNow;
            }
        }
    }
}
