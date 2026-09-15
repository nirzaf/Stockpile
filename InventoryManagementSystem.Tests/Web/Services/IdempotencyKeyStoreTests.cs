using FluentAssertions;
using InventoryManagementSystem.Infrastructure.Data;
using InventoryManagementSystem.Tests.Infrastructure;
using InventoryManagementSystem.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace InventoryManagementSystem.Tests.Web.Services;

public class IdempotencyKeyStoreTests
{
    [Fact]
    public async Task ExecuteAsync_WhenSameKeyIsRetried_ExecutesOperationOnce()
    {
        await using var context = CreateContext();
        var store = new IdempotencyKeyStore(context, new TestTenantContext("test-tenant"));
        var executions = 0;

        for (var attempt = 0; attempt < 10; attempt++)
        {
            await store.ExecuteAsync("POST:/stock/receive", "request-1", "hash-1", async () =>
            {
                Interlocked.Increment(ref executions);
                await Task.Delay(25);
            });
        }
        await store.ExecuteAsync("POST:/stock/receive", "request-1", "hash-1", () =>
        {
            Interlocked.Increment(ref executions);
            return Task.CompletedTask;
        });

        executions.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_WhenOperationFails_AllowsRetry()
    {
        await using var context = CreateContext();
        var store = new IdempotencyKeyStore(context, new TestTenantContext("test-tenant"));
        var executions = 0;

        var firstAttempt = () => store.ExecuteAsync("POST:/stock/receive", "request-2", "hash-2", () =>
        {
            Interlocked.Increment(ref executions);
            throw new InvalidOperationException("transient failure");
        });

        await firstAttempt.Should().ThrowAsync<InvalidOperationException>();

        await store.ExecuteAsync("POST:/stock/receive", "request-2", "hash-2", () =>
        {
            Interlocked.Increment(ref executions);
            return Task.CompletedTask;
        });

        executions.Should().Be(2);
    }

    [Fact]
    public async Task Rejects_reuse_with_a_different_request_hash()
    {
        await using var context = CreateContext();
        var store = new IdempotencyKeyStore(context, new TestTenantContext("test-tenant"));
        await store.ExecuteAsync("POST:/stock/receive", "request-3", "hash-a", () => Task.CompletedTask);

        await FluentActions.Invoking(() => store.ExecuteAsync("POST:/stock/receive", "request-3", "hash-b", () => Task.CompletedTask))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    private static InventoryDbContext CreateContext() => new(
        new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
        new TestTenantContext("test-tenant"));
}
