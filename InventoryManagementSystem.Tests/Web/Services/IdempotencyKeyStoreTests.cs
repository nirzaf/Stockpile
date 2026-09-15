using FluentAssertions;
using InventoryManagementSystem.Web.Services;

namespace InventoryManagementSystem.Tests.Web.Services;

public class IdempotencyKeyStoreTests
{
    [Fact]
    public async Task ExecuteAsync_WhenSameKeyRunsConcurrently_ExecutesOperationOnce()
    {
        var store = new IdempotencyKeyStore();
        var executions = 0;

        var requests = Enumerable.Range(0, 10)
            .Select(_ => store.ExecuteAsync("tenant:POST:/stock/receive", "request-1", async () =>
            {
                Interlocked.Increment(ref executions);
                await Task.Delay(25);
            }));

        await Task.WhenAll(requests);
        await store.ExecuteAsync("tenant:POST:/stock/receive", "request-1", () =>
        {
            Interlocked.Increment(ref executions);
            return Task.CompletedTask;
        });

        executions.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_WhenOperationFails_AllowsRetry()
    {
        var store = new IdempotencyKeyStore();
        var executions = 0;

        var firstAttempt = () => store.ExecuteAsync("tenant:POST:/stock/receive", "request-2", () =>
        {
            Interlocked.Increment(ref executions);
            throw new InvalidOperationException("transient failure");
        });

        await firstAttempt.Should().ThrowAsync<InvalidOperationException>();

        await store.ExecuteAsync("tenant:POST:/stock/receive", "request-2", () =>
        {
            Interlocked.Increment(ref executions);
            return Task.CompletedTask;
        });

        executions.Should().Be(2);
    }
}
