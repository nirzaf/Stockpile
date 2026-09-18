using System.Linq.Expressions;
using Merconiq.Core.Interfaces;
using Merconiq.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Infrastructure.Repositories;

/// <summary>
/// Generic Entity Framework Core repository. Read operations use <c>AsNoTracking</c> for
/// performance since most reads do not need change tracking; write operations (add / update /
/// delete) attach the entity so it participates in the change tracker. Updates merge into an
/// already tracked instance with the same key so repeated writes in one transaction remain safe.
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
    public virtual IQueryable<T> Query() => _dbSet.AsNoTracking();

    /// <inheritdoc />
    public virtual Task<T?> GetByIdAsync(int id) => GetByIdAsync(id, CancellationToken.None);

    /// <inheritdoc />
    public virtual async Task<T?> GetByIdAsync(int id, CancellationToken cancellationToken)
    {
        return await _dbSet.FindAsync([id], cancellationToken);
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
        return FindAsync(predicate, null, CancellationToken.None);
    }

    /// <inheritdoc />
    public virtual Task<IEnumerable<T>> FindAsync(
        Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken)
    {
        return FindAsync(predicate, null, cancellationToken);
    }

    /// <inheritdoc />
    public virtual async Task<IEnumerable<T>> FindAsync(
        Expression<Func<T, bool>> predicate,
        Func<IQueryable<T>, IOrderedQueryable<T>>? orderBy = null)
        => await FindAsync(predicate, orderBy, CancellationToken.None);

    /// <inheritdoc />
    public virtual async Task<IEnumerable<T>> FindAsync(
        Expression<Func<T, bool>> predicate,
        Func<IQueryable<T>, IOrderedQueryable<T>>? orderBy,
        CancellationToken cancellationToken)
    {
        var query = _dbSet.AsNoTracking().Where(predicate);
        if (orderBy != null) query = orderBy(query);
        return await query.ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public virtual async Task<IEnumerable<T>> FindPageAsync(
        Expression<Func<T, bool>> predicate,
        Func<IQueryable<T>, IOrderedQueryable<T>> orderBy,
        int maxResults)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(orderBy);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResults);

        return await orderBy(_dbSet.AsNoTracking().Where(predicate))
            .Take(maxResults)
            .ToListAsync();
    }

    /// <inheritdoc />
    public virtual async Task<IEnumerable<T>> GetPagedAsync(int page, int pageSize)
    {
        return await _dbSet.AsNoTracking()
            .OrderBy(entity => EF.Property<object>(entity, "Id"))
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
        var entityType = _context.Model.FindEntityType(typeof(T));
        var key = entityType?.FindPrimaryKey();
        var incoming = _context.Entry(entity);
        var tracked = key is null
            ? null
            : _context.ChangeTracker.Entries<T>().FirstOrDefault(entry =>
                !ReferenceEquals(entry.Entity, entity) &&
                key.Properties.All(property =>
                    Equals(entry.Property(property.Name).CurrentValue,
                        incoming.Property(property.Name).CurrentValue)));
        if (tracked is null)
            _dbSet.Update(entity);
        else
            tracked.CurrentValues.SetValues(entity);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public virtual Task DeleteAsync(T entity)
    {
        var entityType = _context.Model.FindEntityType(typeof(T));
        var key = entityType?.FindPrimaryKey();
        var incoming = _context.Entry(entity);
        var tracked = key is null
            ? null
            : _context.ChangeTracker.Entries<T>().FirstOrDefault(entry =>
                !ReferenceEquals(entry.Entity, entity) &&
                key.Properties.All(property =>
                    Equals(entry.Property(property.Name).CurrentValue,
                        incoming.Property(property.Name).CurrentValue)));
        _dbSet.Remove(tracked?.Entity ?? entity);
        return Task.CompletedTask;
    }

}
