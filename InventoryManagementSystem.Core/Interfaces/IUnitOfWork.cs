namespace InventoryManagementSystem.Core.Interfaces;

/// <summary>
/// Owns persistence across multiple repositories in a single transaction.
/// Services should inject IUnitOfWork for multi-operation atomicity.
/// </summary>
public interface IUnitOfWork
{
    /// <summary>Gets whether this unit of work currently owns an active database transaction.</summary>
    bool HasActiveTransaction { get; }

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

    /// <summary>Executes an operation inside a retryable transaction boundary.</summary>
    /// <param name="operation">The operation whose changes must commit atomically.</param>
    /// <param name="cancellationToken">A token used while creating and committing the transaction.</param>
    /// <param name="verifySucceeded">Optionally verifies durable success after an ambiguous commit.</param>
    Task ExecuteInTransactionAsync(
        Func<Task> operation,
        CancellationToken cancellationToken = default,
        Func<Task<bool>>? verifySucceeded = null);

    /// <summary>Clears the EF Core change tracker. Use after a failed <c>SaveChangesAsync</c> to retry.</summary>
    void ClearTracker();
}
