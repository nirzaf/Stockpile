using InventoryManagementSystem.Core.Entities;
using InventoryManagementSystem.Core.Interfaces;
using InventoryManagementSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace InventoryManagementSystem.Web.Services;

/// <summary>PostgreSQL-backed idempotency coordinator shared by API instances.</summary>
public sealed class IdempotencyKeyStore(
    InventoryDbContext context,
    ITenantContext tenantContext,
    IUnitOfWork unitOfWork) : IIdempotencyKeyStore
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaxClaimWait = TimeSpan.FromSeconds(30);

    public async Task ExecuteAsync(
        string scope,
        string key,
        string requestHash,
        Func<Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);
        ArgumentNullException.ThrowIfNull(operation);
        if (!tenantContext.IsResolved)
        {
            throw new InvalidOperationException("A tenant context is required for idempotency.");
        }

        var record = await ClaimAsync(scope, key, requestHash, cancellationToken);
        if (record is null)
        {
            return;
        }

        var ownsTransaction = !unitOfWork.HasActiveTransaction;
        var concurrencyRetries = 3;
        try
        {
            while (true)
            {
                try
                {
                    await unitOfWork.ExecuteInTransactionAsync(async () =>
                    {
                        await operation();
                        TrackRecord(record);
                        record.Status = IdempotencyRecordStatus.Completed;
                        record.ResponseStatusCode = StatusCodes.Status204NoContent;
                        record.CompletedAt = DateTimeOffset.UtcNow;
                        record.LeaseUntil = null;
                        record.LastError = null;
                    }, CancellationToken.None, async () =>
                    {
                        var completed = await context.IdempotencyRecords
                            .AsNoTracking()
                            .AnyAsync(item => item.Id == record.Id &&
                                              item.Status == IdempotencyRecordStatus.Completed,
                                CancellationToken.None);
                        if (!completed)
                        {
                            context.ChangeTracker.Clear();
                        }
                        return completed;
                    });
                    break;
                }
                catch (InventoryManagementSystem.Core.Exceptions.ConcurrencyException) when (--concurrencyRetries > 0)
                {
                    // The complete keyed operation owns the transaction boundary.
                    // Restart it after a conflict so StockService can never retry
                    // inside a transaction that is already invalid.
                    unitOfWork.ClearTracker();
                    await Task.Delay(100, CancellationToken.None);
                }
            }
        }
        catch (Exception ex)
        {
            // An operation can leave unrelated entities in the tracker before failing.
            // Detach those changes before recording the failed claim so the failure path
            // cannot accidentally commit a partial business mutation.
            var recordId = record.Id;
            if (ownsTransaction)
            {
                await unitOfWork.RollbackTransactionAsync(CancellationToken.None);
            }
            else
            {
                throw;
            }
            context.ChangeTracker.Clear();
            record = await context.IdempotencyRecords
                .SingleAsync(item => item.Id == recordId, CancellationToken.None);
            record.Status = IdempotencyRecordStatus.Failed;
            record.LeaseUntil = null;
            record.LastError = ex.Message[..Math.Min(ex.Message.Length, 4096)];
            await context.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private void TrackRecord(IdempotencyRecord record)
    {
        var entry = context.Entry(record);
        if (entry.State == EntityState.Detached)
        {
            entry = context.IdempotencyRecords.Attach(record);
        }

        // A retry can clear the tracker after this object was already populated
        // with its completed values. Explicitly mark the completion columns so
        // reattaching the object cannot turn them into an unchanged snapshot.
        entry.Property(item => item.Status).IsModified = true;
        entry.Property(item => item.ResponseStatusCode).IsModified = true;
        entry.Property(item => item.CompletedAt).IsModified = true;
        entry.Property(item => item.LeaseUntil).IsModified = true;
        entry.Property(item => item.LastError).IsModified = true;
    }

    private async Task<IdempotencyRecord?> ClaimAsync(
        string scope,
        string key,
        string requestHash,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var deadline = now.Add(MaxClaimWait);
        var expired = await context.IdempotencyRecords
            .Where(record => record.ExpiresAt <= now)
            .Take(100)
            .ToListAsync(cancellationToken);
        if (expired.Count > 0)
        {
            context.IdempotencyRecords.RemoveRange(expired);
            await context.SaveChangesAsync(cancellationToken);
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            now = DateTimeOffset.UtcNow;
            if (now >= deadline)
            {
                throw new TimeoutException("The idempotency key claim could not be acquired within the allowed wait time.");
            }

            var record = await context.IdempotencyRecords
                .SingleOrDefaultAsync(item => item.Scope == scope && item.Key == key, cancellationToken);
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
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                    context.Entry(record).State = EntityState.Detached;
                    continue;
                }

                record.Status = IdempotencyRecordStatus.InProgress;
                record.AttemptCount++;
                record.LeaseUntil = now.Add(ClaimLease);
                record.LastError = null;
                await context.SaveChangesAsync(cancellationToken);
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
                await context.SaveChangesAsync(cancellationToken);
                return record;
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                context.ChangeTracker.Clear();
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
            }
        }
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgresException &&
                postgresException.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return true;
            }
        }

        return false;
    }
}
