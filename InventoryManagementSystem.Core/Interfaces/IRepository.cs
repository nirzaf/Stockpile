using System.Linq.Expressions;

namespace InventoryManagementSystem.Core.Interfaces;

/// <summary>Generic repository contract for entity persistence operations.</summary>
/// <typeparam name="T">The entity type managed by this repository.</typeparam>
public interface IRepository<T> where T : class
{
    /// <summary>Gets an entity by its primary key.</summary>
    /// <param name="id">The entity identifier.</param>
    /// <returns>The entity, or <see langword="null"/> if not found.</returns>
    Task<T?> GetByIdAsync(int id);

    /// <summary>Retrieves all entities.</summary>
    /// <returns>A collection of entities.</returns>
    Task<IEnumerable<T>> GetAllAsync();

    /// <summary>Finds entities matching a predicate (executed on the database).</summary>
    /// <param name="predicate">A LINQ predicate expression.</param>
    /// <returns>Matching entities.</returns>
    Task<IEnumerable<T>> FindAsync(Expression<Func<T, bool>> predicate);

    /// <summary>Find entities with a predicate and optional ordering (executed on the database).</summary>
    /// <param name="predicate">A LINQ predicate expression.</param>
    /// <param name="orderBy">An optional ordering function applied to the queryable.</param>
    /// <returns>Matching, optionally ordered entities.</returns>
    Task<IEnumerable<T>> FindAsync(
        Expression<Func<T, bool>> predicate,
        Func<IQueryable<T>, IOrderedQueryable<T>>? orderBy = null);

    /// <summary>Retrieves a page of entities.</summary>
    /// <param name="page">The 1-based page number.</param>
    /// <param name="pageSize">The number of entities per page.</param>
    /// <returns>A collection of entities for the requested page.</returns>
    Task<IEnumerable<T>> GetPagedAsync(int page, int pageSize);

    /// <summary>Gets the total number of entities.</summary>
    /// <returns>The total count.</returns>
    Task<int> CountAsync();

    /// <summary>Adds a new entity to the underlying context.</summary>
    /// <param name="entity">The entity to add.</param>
    /// <returns>The added entity, with any database-generated values populated.</returns>
    Task<T> AddAsync(T entity);

    /// <summary>Stages an entity update on the underlying context.</summary>
    /// <param name="entity">The entity with updated values.</param>
    Task UpdateAsync(T entity);

    /// <summary>Stages an entity deletion on the underlying context (soft or hard depending on entity).</summary>
    /// <param name="entity">The entity to delete.</param>
    Task DeleteAsync(T entity);

    /// <summary>
    /// Commit all pending changes without the Unit of Work.
    /// Prefer IUnitOfWork.SaveChangesAsync for multi-operation scenarios.
    /// </summary>
    Task SaveChangesAsync();
}
