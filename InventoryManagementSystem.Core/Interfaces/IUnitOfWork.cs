namespace InventoryManagementSystem.Core.Interfaces;

/// <summary>
/// Coordinates persistence across multiple repositories in a single transaction.
/// Services should inject IUnitOfWork for multi-operation atomicity.
/// </summary>
public interface IUnitOfWork
{
    /// <summary>Persists all pending changes across all repositories in a single transaction.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The number of state entries written to the database.</returns>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Begins a new database transaction. Call <see cref="CommitTransactionAsync"/> to commit.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task BeginTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>Commits the active database transaction.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task CommitTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>Rolls back the active database transaction.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task RollbackTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>Clears the EF Core change tracker. Use after a failed <c>SaveChangesAsync</c> to retry.</summary>
    void ClearTracker();
}
