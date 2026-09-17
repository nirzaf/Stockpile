using System.Data;
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

        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(
            state: 0,
            operation: async (_, _, transactionCancellationToken) =>
            {
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
                    await transaction.RollbackAsync(CancellationToken.None);
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
