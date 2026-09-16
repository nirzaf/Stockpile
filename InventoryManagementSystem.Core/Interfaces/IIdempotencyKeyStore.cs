namespace InventoryManagementSystem.Core.Interfaces;

/// <summary>Coordinates repeated requests that carry the same idempotency key.</summary>
public interface IIdempotencyKeyStore
{
    /// <summary>
    /// Runs an operation once for the supplied scope, key, and request hash. Concurrent or
    /// subsequent successful requests await/reuse the durable result; failed operations may retry.
    /// Claims waiting on another live lease are bounded and honor the supplied cancellation token.
    /// </summary>
    Task ExecuteAsync(
        string scope,
        string key,
        string requestHash,
        Func<Task> operation,
        CancellationToken cancellationToken = default);
}
