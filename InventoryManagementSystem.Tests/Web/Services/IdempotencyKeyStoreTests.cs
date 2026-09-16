using FluentAssertions;
using InventoryManagementSystem.Core.Entities;
using InventoryManagementSystem.Core.Exceptions;
using InventoryManagementSystem.Core.Interfaces;
using InventoryManagementSystem.Infrastructure.Data;
using InventoryManagementSystem.Tests.Infrastructure;
using InventoryManagementSystem.Web.Services;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace InventoryManagementSystem.Tests.Web.Services;

public class IdempotencyKeyStoreTests
{
    [Fact]
    public async Task ExecuteAsync_WhenSameKeyIsRetried_ExecutesOperationOnce()
    {
        await using var context = CreateContext();
        var store = CreateStore(context);
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
        var store = CreateStore(context);
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
        var store = CreateStore(context);
        await store.ExecuteAsync("POST:/stock/receive", "request-3", "hash-a", () => Task.CompletedTask);

        await FluentActions.Invoking(() => store.ExecuteAsync("POST:/stock/receive", "request-3", "hash-b", () => Task.CompletedTask))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ExecuteAsync_WhenClaimIsHeld_HonorsCancellation()
    {
        await using var context = CreateContext();
        context.IdempotencyRecords.Add(new IdempotencyRecord
        {
            TenantId = "test-tenant",
            Scope = "POST:/stock/held",
            Key = "request-held",
            RequestHash = "hash-held",
            LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(1),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        });
        await context.SaveChangesAsync();

        var store = CreateStore(context);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await FluentActions.Invoking(() => store.ExecuteAsync(
                "POST:/stock/held", "request-held", "hash-held", () => Task.CompletedTask, cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ExecuteAsync_WhenOperationFails_DoesNotFlushPendingBusinessChanges()
    {
        await using var context = CreateContext();
        var store = CreateStore(context);

        await FluentActions.Invoking(() => store.ExecuteAsync(
                "POST:/stock/failure", "request-failure", "hash-failure", () =>
                {
                    context.Items.Add(new Item
                    {
                        ItemCode = "UNCOMMITTED-FAILURE",
                        Description = "Must not be flushed",
                        Rate = 1m
                    });
                    throw new InvalidOperationException("expected failure");
                }))
            .Should().ThrowAsync<InvalidOperationException>();

        (await context.Items.CountAsync(item => item.ItemCode == "UNCOMMITTED-FAILURE"))
            .Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_WhenOperationClearsTracker_PersistsCompletion()
    {
        await using var context = CreateContext();
        var store = CreateStore(context);

        await store.ExecuteAsync("POST:/stock/tracker", "request-tracker", "hash-tracker", () =>
        {
            context.ChangeTracker.Clear();
            return Task.CompletedTask;
        });

        context.ChangeTracker.Clear();
        (await context.IdempotencyRecords.AsNoTracking().SingleAsync()).Status
            .Should().Be(IdempotencyRecordStatus.Completed);
    }

    [Fact]
    public async Task ExecuteAsync_FinalizesAfterRequestCancellation()
    {
        await using var context = CreateContext();
        var store = CreateStore(context);
        using var cancellation = new CancellationTokenSource();

        await store.ExecuteAsync("POST:/stock/cancel", "request-cancel", "hash-cancel", () =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        }, cancellation.Token);

        context.ChangeTracker.Clear();
        (await context.IdempotencyRecords.AsNoTracking().SingleAsync()).Status
            .Should().Be(IdempotencyRecordStatus.Completed);
    }

    [Fact]
    public async Task ExecuteAsync_RestartsTheWholeOperationAfterAConcurrencyConflict()
    {
        await using var context = CreateContext();
        var unitOfWork = new Mock<IUnitOfWork>();
        var attempts = 0;
        unitOfWork.SetupGet(item => item.HasActiveTransaction).Returns(false);
        unitOfWork
            .Setup(item => item.ExecuteInTransactionAsync(
                It.IsAny<Func<Task>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Func<Task<bool>>?>()))
            .Returns(async (Func<Task> operation, CancellationToken _, Func<Task<bool>>? _) =>
            {
                if (++attempts == 1)
                {
                    await operation();
                    throw new ConcurrencyException("simulated conflict");
                }

                await operation();
            });
        unitOfWork.Setup(item => item.ClearTracker()).Callback(context.ChangeTracker.Clear);

        var store = new IdempotencyKeyStore(
            context,
            new TestTenantContext("test-tenant"),
            unitOfWork.Object);
        var executions = 0;

        await store.ExecuteAsync("POST:/stock/retry", "request-retry", "hash-retry", () =>
        {
            executions++;
            return Task.CompletedTask;
        });

        executions.Should().Be(2);
        attempts.Should().Be(2);
        unitOfWork.Verify(item => item.ClearTracker(), Times.Once);
    }

    private static IdempotencyKeyStore CreateStore(InventoryDbContext context) =>
        new(context, new TestTenantContext("test-tenant"), new UnitOfWork(context));

    private static InventoryDbContext CreateContext() => new(
        new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
        new TestTenantContext("test-tenant"));
}
