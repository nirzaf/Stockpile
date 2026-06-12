using System.Linq.Expressions;
using InventoryManagementSystem.Core.Interfaces;
using InventoryManagementSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace InventoryManagementSystem.Infrastructure.Repositories;

/// <summary>
/// Generic Entity Framework Core repository. Read operations use <c>AsNoTracking</c> for
/// performance since most reads do not need change tracking; write operations (add / update /
/// delete) attach the entity so it participates in the change tracker.
/// </summary>
/// <typeparam name="T">The entity type managed by this repository.</typeparam>
public class Repository<T> : IRepository<T> where T : class
{
    protected readonly InventoryDbContext _context;
    protected readonly DbSet<T> _dbSet;

    public Repository(InventoryDbContext context)
    {
        _context = context;
        _dbSet = context.Set<T>();
    }

    /// <inheritdoc />
    public virtual async Task<T?> GetByIdAsync(int id)
    {
        return await _dbSet.FindAsync(id);
    }

    /// <inheritdoc />
    public virtual async Task<IEnumerable<T>> GetAllAsync()
    {
        // AsNoTracking bypasses the EF Core change tracker for read-only queries, which
        // saves both memory (no per-entity identity map entries) and CPU (no snapshot
        // comparison work on the next SaveChangesAsync). Writes go through AddAsync /
        // UpdateAsync / DeleteAsync, which intentionally re-attach the entity so it
        // participates in change tracking.
        return await _dbSet.AsNoTracking().ToListAsync();
    }

    /// <inheritdoc />
    public virtual Task<IEnumerable<T>> FindAsync(Expression<Func<T, bool>> predicate)
    {
        return FindAsync(predicate, null);
    }

    /// <inheritdoc />
    public virtual async Task<IEnumerable<T>> FindAsync(
        Expression<Func<T, bool>> predicate,
        Func<IQueryable<T>, IOrderedQueryable<T>>? orderBy)
    {
        var query = _dbSet.AsNoTracking().Where(predicate);
        if (orderBy != null) query = orderBy(query);
        return await query.ToListAsync();
    }

    /// <inheritdoc />
    public virtual async Task<IEnumerable<T>> GetPagedAsync(int page, int pageSize)
    {
        return await _dbSet.AsNoTracking()
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    /// <inheritdoc />
    public virtual async Task<int> CountAsync()
    {
        return await _dbSet.CountAsync();
    }

    /// <inheritdoc />
    public virtual async Task<T> AddAsync(T entity)
    {
        await _dbSet.AddAsync(entity);
        return entity;
    }

    /// <inheritdoc />
    public virtual Task UpdateAsync(T entity)
    {
        _dbSet.Update(entity);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public virtual Task DeleteAsync(T entity)
    {
        _dbSet.Remove(entity);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public virtual async Task SaveChangesAsync()
    {
        await _context.SaveChangesAsync();
    }
}
