using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using InventoryManagementSystem.Core.Interfaces;
using InventoryManagementSystem.Core.Exceptions;

namespace InventoryManagementSystem.Infrastructure.Data;

public class UnitOfWork : IUnitOfWork
{
    private readonly InventoryDbContext _context;
    private IDbContextTransaction? _currentTransaction;

    public UnitOfWork(InventoryDbContext context)
    {
        _context = context;
    }

    public bool HasActiveTransaction => _currentTransaction is not null;

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
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

    public async Task ExecuteInTransactionAsync(Func<Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (_currentTransaction is not null || _context.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            await operation();
            if (_context.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            return;
        }

        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await BeginTransactionAsync(cancellationToken);
            try
            {
                await operation();
                await CommitTransactionAsync(cancellationToken);
            }
            catch
            {
                // A retry starts with a clean tracker after a failed transaction.
                // This is also safe for the outer caller, which receives the original exception.
                await RollbackTransactionAsync(CancellationToken.None);
                _context.ChangeTracker.Clear();
                throw;
            }
        });
    }

    public void ClearTracker()
    {
        _context.ChangeTracker.Clear();
    }
}
