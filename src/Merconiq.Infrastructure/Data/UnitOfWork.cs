using System.Data;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Exceptions;
using Npgsql;

namespace Merconiq.Infrastructure.Data;

public class UnitOfWork : IUnitOfWork
{
    private readonly InventoryDbContext _context;
    private IDbContextTransaction? _currentTransaction;
    private bool _executionStrategyTransactionActive;

    public UnitOfWork(InventoryDbContext context)
    {
        _context = context;
    }

    public bool HasActiveTransaction => _currentTransaction is not null || _executionStrategyTransactionActive;

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // ExecuteInTransactionAsync owns the retry boundary and translates the
            // provider exception after its callback has unwound. Let it observe the
            // original exception so a caller-owned coordinator can restart the whole
            // operation instead of retrying inside an invalid transaction.
            if (_executionStrategyTransactionActive)
            {
                throw;
            }

            throw new ConcurrencyException("A concurrency conflict occurred while saving changes.", ex);
        }
    }

    public async Task BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (_currentTransaction != null || _context.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            return;
        }
        _currentTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
    }

    public async Task CommitTransactionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            if (_currentTransaction != null)
            {
                await _currentTransaction.CommitAsync(cancellationToken);
            }
        }
        catch (DbUpdateConcurrencyException ex)
        {
            await RollbackTransactionAsync(cancellationToken);
            throw new ConcurrencyException("A concurrency conflict occurred during commit.", ex);
        }
        catch
        {
            await RollbackTransactionAsync(cancellationToken);
            throw;
        }
        finally
        {
            if (_currentTransaction != null)
            {
                _currentTransaction.Dispose();
                _currentTransaction = null;
            }
        }
    }

    public async Task RollbackTransactionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (_currentTransaction != null)
            {
                await _currentTransaction.RollbackAsync(cancellationToken);
            }
        }
        finally
        {
            if (_currentTransaction != null)
            {
                _currentTransaction.Dispose();
                _currentTransaction = null;
            }
        }
    }

    public async Task ExecuteInTransactionAsync(
        Func<Task> operation,
        CancellationToken cancellationToken = default,
        Func<Task<bool>>? verifySucceeded = null)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (HasActiveTransaction)
        {
            await operation();
            return;
        }

        if (_context.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            await operation();
            await _context.SaveChangesAsync(cancellationToken);
            return;
        }

        var strategy = _context.Database.CreateExecutionStrategy();
        var concurrencyRetries = 3;
        while (true)
        {
            try
            {
                await strategy.ExecuteInTransactionAsync(
                    async transactionCancellationToken =>
                    {
                        _executionStrategyTransactionActive = true;
                        try
                        {
                            await operation();
                            await _context.SaveChangesAsync(transactionCancellationToken);
                        }
                        catch (DbUpdateConcurrencyException ex)
                        {
                            _context.ChangeTracker.Clear();
                            throw new ConcurrencyException("A concurrency conflict occurred during the transaction.", ex);
                        }
                        catch (DbUpdateException ex) when (IsValuationBucketInsertConflict(ex))
                        {
                            _context.ChangeTracker.Clear();
                            throw new ConcurrencyException("A concurrent transaction created the stock valuation bucket.", ex);
                        }
                        catch
                        {
                            _context.ChangeTracker.Clear();
                            throw;
                        }
                        finally
                        {
                            _executionStrategyTransactionActive = false;
                        }
                    },
                    async _ => verifySucceeded is null || await verifySucceeded(),
                    cancellationToken);
                return;
            }
            catch (ConcurrencyException) when (--concurrencyRetries > 0)
            {
                // A concurrency failure invalidates the current attempt. Retry the
                // complete operation in a fresh execution-strategy transaction so
                // callers such as IdempotencyKeyStore never replay inside a failed
                // transaction boundary.
                _context.ChangeTracker.Clear();
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    public async Task ExecuteInReadSnapshotAsync(
        Func<Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (HasActiveTransaction)
        {
            throw new InvalidOperationException("A read snapshot must own its repeatable-read transaction.");
        }

        if (_context.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            await operation();
            return;
        }

        if (_context.ChangeTracker.HasChanges())
        {
            throw new InvalidOperationException(
                "A read-snapshot retry boundary cannot start while the DbContext has unsaved changes.");
        }

        var strategy = _context.Database.CreateExecutionStrategy();
        var attempt = 0;
        await strategy.ExecuteAsync(
            state: 0,
            operation: async (_, _, transactionCancellationToken) =>
            {
                if (attempt++ > 0)
                {
                    // BeginTransactionAsync can fail before the callback reaches its catch
                    // block. A retry still needs a fresh view of any entities the previous
                    // attempt may have materialized.
                    _context.ChangeTracker.Clear();
                }

                await using var transaction = await _context.Database.BeginTransactionAsync(
                    IsolationLevel.RepeatableRead, transactionCancellationToken);
                _currentTransaction = transaction;
                try
                {
                    await operation();
                    await transaction.CommitAsync(transactionCancellationToken);
                }
                catch
                {
                    try
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                    }
                    catch
                    {
                        // A commit can succeed in PostgreSQL and still surface a transient
                        // connection error to the client. Preserve that original exception
                        // so the execution strategy can retry from fresh database state.
                    }

                    // SaveChanges accepts tracked values before commit. If commit failed or
                    // its result was ambiguous, replaying against those values can skip the
                    // write after a rollback. Force the next attempt to reload from PostgreSQL.
                    _context.ChangeTracker.Clear();
                    throw;
                }
                finally
                {
                    _currentTransaction = null;
                }

                return true;
            },
            verifySucceeded: null,
            cancellationToken: cancellationToken);
    }

    public async Task AcquireLocationLocksAsync(
        IReadOnlyCollection<int> locationIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(locationIds);
        if (locationIds.Count == 0 || !IsPostgreSql)
        {
            return;
        }

        foreach (var locationId in locationIds.Distinct().Order())
        {
            if (locationId <= 0)
                throw new ArgumentOutOfRangeException(nameof(locationIds), "Location IDs must be positive.");

            var lockKey = string.Create(CultureInfo.InvariantCulture,
                $"stock-location:{_context.CurrentTenantId}:{locationId}");
            await AcquireTransactionAdvisoryLockAsync(lockKey, cancellationToken);
        }
    }

    public Task AcquireTenantOperationLockAsync(
        string operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (!IsPostgreSql)
            return Task.CompletedTask;

        var lockKey = string.Create(CultureInfo.InvariantCulture,
            $"tenant-operation:{_context.CurrentTenantId}:{operation.Trim()}");
        return AcquireTransactionAdvisoryLockAsync(lockKey, cancellationToken);
    }

    private async Task AcquireTransactionAdvisoryLockAsync(
        string lockKey,
        CancellationToken cancellationToken)
    {
        if (!HasActiveTransaction)
            throw new InvalidOperationException("Advisory locks must be acquired inside the owning transaction.");
        if (string.IsNullOrWhiteSpace(_context.CurrentTenantId))
            throw new InvalidOperationException("A resolved tenant is required to acquire advisory locks.");

        await _context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))",
            cancellationToken);
    }

    private bool IsPostgreSql =>
        _context.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL";

    public void ClearTracker()
    {
        _context.ChangeTracker.Clear();
    }

    private static bool IsValuationBucketInsertConflict(DbUpdateException exception)
    {
        var postgresException = exception.InnerException as PostgresException;
        return postgresException?.SqlState == PostgresErrorCodes.UniqueViolation
            && postgresException.ConstraintName == "IX_StockValuationBuckets_TenantId_ItemId_LocationId";
    }
}
