namespace Merconiq.Core.Interfaces;

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

    /// <summary>
    /// Executes an operation in its own repeatable-read transaction using the provider's execution strategy.
    /// Tracked entities are refreshed from each attempt's snapshot; the callback may run more than once,
    /// so side effects outside the transaction must be replay-safe.
    /// </summary>
    /// <param name="operation">The operation that must share one snapshot and may be retried.</param>
    /// <param name="cancellationToken">A token used while creating and completing the transaction.</param>
    Task ExecuteInReadSnapshotAsync(
        Func<Task> operation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Acquires transaction-scoped locks for the supplied tenant locations in ascending order.
    /// Calls must be made inside the transaction that reads or writes stock for those locations.
    /// </summary>
    Task AcquireLocationLocksAsync(
        IReadOnlyCollection<int> locationIds,
        CancellationToken cancellationToken = default);

    /// <summary>Acquires a tenant-scoped transaction lock for a serialized one-shot operation.</summary>
    Task AcquireTenantOperationLockAsync(
        string operation,
        CancellationToken cancellationToken = default);

    /// <summary>Clears the EF Core change tracker. Use after a failed <c>SaveChangesAsync</c> to retry.</summary>
    void ClearTracker();
}
