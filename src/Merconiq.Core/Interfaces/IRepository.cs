using System.Linq.Expressions;

namespace Merconiq.Core.Interfaces;

/// <summary>Generic repository contract for entity persistence operations.</summary>
/// <typeparam name="T">The entity type managed by this repository.</typeparam>
public interface IRepository<T> where T : class
{
    /// <summary>
    /// Returns a read-only, composable query for the entity set.
    /// </summary>
    /// <remarks>
    /// The query is backed by the scoped DbContext and is executed when enumerated. Callers
    /// should keep composition within the request scope and use projection/pagination before
    /// materializing results.
    /// </remarks>
    IQueryable<T> Query();

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

    /// <summary>
    /// Finds an ordered, bounded set of entities. The ordering and limit are applied to the
    /// provider query before materialization, so callers can use this for database-side caps.
    /// </summary>
    /// <param name="predicate">The filter to apply before the limit.</param>
    /// <param name="orderBy">A deterministic ordering applied before the limit.</param>
    /// <param name="maxResults">The maximum number of results to materialize.</param>
    /// <returns>At most <paramref name="maxResults"/> matching entities.</returns>
    Task<IEnumerable<T>> FindPageAsync(
        Expression<Func<T, bool>> predicate,
        Func<IQueryable<T>, IOrderedQueryable<T>> orderBy,
        int maxResults);

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

}
