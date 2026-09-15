namespace InventoryManagementSystem.Core.Interfaces;

/// <summary>Coordinates repeated requests that carry the same idempotency key.</summary>
public interface IIdempotencyKeyStore
{
    /// <summary>
    /// Runs an operation once for the supplied scope and key. Concurrent or subsequent
    /// successful requests await/reuse the original operation; failed operations may retry.
    /// </summary>
    Task ExecuteAsync(string scope, string key, Func<Task> operation);
}
