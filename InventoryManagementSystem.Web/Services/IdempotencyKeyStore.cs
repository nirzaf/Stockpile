using System.Collections.Concurrent;
using InventoryManagementSystem.Core.Interfaces;

namespace InventoryManagementSystem.Web.Services;

/// <summary>
/// Process-local idempotency coordinator for API commands.
/// </summary>
/// <remarks>
/// Successful keys are retained for one hour. This protects retries and concurrent duplicate
/// deliveries within an application instance; a shared distributed store should be substituted
/// when the API is deployed across multiple independent instances.
/// </remarks>
public sealed class IdempotencyKeyStore : IIdempotencyKeyStore
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public Task ExecuteAsync(string scope, string key, Func<Task> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(operation);

        var cacheKey = $"{scope}:{key}";
        Entry newEntry = null!;
        newEntry = new Entry(
            () => RunAndRemoveOnFailureAsync(cacheKey, newEntry, operation));

        while (true)
        {
            if (_entries.TryGetValue(cacheKey, out var existing))
            {
                if (existing.ExpiresAt > DateTimeOffset.UtcNow)
                {
                    return existing.Operation.Value;
                }

                _entries.TryRemove(cacheKey, out _);
                continue;
            }

            if (_entries.TryAdd(cacheKey, newEntry))
            {
                return newEntry.Operation.Value;
            }
        }
    }

    private async Task RunAndRemoveOnFailureAsync(string cacheKey, Entry entry, Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch
        {
            ((ICollection<KeyValuePair<string, Entry>>)_entries)
                .Remove(new KeyValuePair<string, Entry>(cacheKey, entry));
            throw;
        }
    }

    private sealed class Entry
    {
        public Entry(Func<Task> operation)
        {
            Operation = new Lazy<Task>(operation, LazyThreadSafetyMode.ExecutionAndPublication);
            ExpiresAt = DateTimeOffset.UtcNow.Add(Retention);
        }

        public DateTimeOffset ExpiresAt { get; }
        public Lazy<Task> Operation { get; }
    }
}
