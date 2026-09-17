using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Infrastructure.Data;
using Merconiq.Tests.Infrastructure;
using Merconiq.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class IdempotencyPostgreSqlIntegrationTests
{
    private readonly PostgreSqlIntegrationFixture _fixture;

    public IdempotencyPostgreSqlIntegrationTests(PostgreSqlIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [PostgreSqlFact]
    public async Task Business_changes_and_completion_record_commit_atomically()
    {
        _fixture.EnsureEnabled();
        var tenantId = $"atomic-idempotency-{Guid.NewGuid():N}";
        var scope = $"POST:/stock/{Guid.NewGuid():N}";
        var key = "atomic-request";
        var hash = "atomic-hash";

        await using (var context = _fixture.CreateContext(tenantId))
        {
            var store = new IdempotencyKeyStore(
                context,
                new TestTenantContext(tenantId),
                new UnitOfWork(context));

            await store.ExecuteAsync(scope, key, hash, () =>
            {
                context.Items.Add(new Item
                {
                    ItemCode = "ATOMIC-ITEM",
                    Description = "committed with claim",
                    Rate = 1m
                });
                return Task.CompletedTask;
            });
        }

        await using var verify = _fixture.CreateContext(tenantId);
        (await verify.Items.CountAsync(item => item.ItemCode == "ATOMIC-ITEM")).Should().Be(1);
        (await verify.IdempotencyRecords.SingleAsync(item => item.Scope == scope)).Status
            .Should().Be(IdempotencyRecordStatus.Completed);
    }

    [PostgreSqlFact]
    public async Task Business_changes_roll_back_when_the_idempotent_operation_fails()
    {
        _fixture.EnsureEnabled();
        var tenantId = $"atomic-failure-{Guid.NewGuid():N}";
        var scope = $"POST:/stock/{Guid.NewGuid():N}";
        var key = "atomic-failure-request";
        var hash = "atomic-failure-hash";

        await using (var context = _fixture.CreateContext(tenantId))
        {
            var store = new IdempotencyKeyStore(
                context,
                new TestTenantContext(tenantId),
                new UnitOfWork(context));

            await FluentActions.Invoking(() => store.ExecuteAsync(scope, key, hash, async () =>
            {
                context.Items.Add(new Item
                {
                    ItemCode = "ATOMIC-FAILURE-ITEM",
                    Description = "rolled back with claim",
                    Rate = 1m
                });
                await context.SaveChangesAsync();
                throw new InvalidOperationException("expected operation failure");
            })).Should().ThrowAsync<InvalidOperationException>();
        }

        await using var verify = _fixture.CreateContext(tenantId);
        (await verify.Items.CountAsync(item => item.ItemCode == "ATOMIC-FAILURE-ITEM")).Should().Be(0);
        (await verify.IdempotencyRecords.SingleAsync(item => item.Scope == scope)).Status
            .Should().Be(IdempotencyRecordStatus.Failed);
    }

    [PostgreSqlFact]
    public async Task Competing_contexts_execute_a_new_claimed_operation_only_once()
    {
        _fixture.EnsureEnabled();
        var tenantId = $"idempotency-tenant-{Guid.NewGuid():N}";
        var scope = $"POST:/stock/{Guid.NewGuid():N}";
        var key = "request-1";
        var hash = "hash-1";
        var firstOperationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;

        await using var firstContext = _fixture.CreateContext(tenantId);
        await using var secondContext = _fixture.CreateContext(tenantId);
        var firstStore = new IdempotencyKeyStore(firstContext, new TestTenantContext(tenantId), new UnitOfWork(firstContext));
        var secondStore = new IdempotencyKeyStore(secondContext, new TestTenantContext(tenantId), new UnitOfWork(secondContext));

        var first = firstStore.ExecuteAsync(scope, key, hash, async () =>
        {
            Interlocked.Increment(ref executions);
            firstOperationStarted.SetResult(true);
            await Task.Delay(250);
        });

        await firstOperationStarted.Task;
        var second = secondStore.ExecuteAsync(scope, key, hash, () =>
        {
            Interlocked.Increment(ref executions);
            return Task.CompletedTask;
        });

        await Task.WhenAll(first, second);

        executions.Should().Be(1);
        await using var verify = _fixture.CreateContext(tenantId);
        var record = await verify.IdempotencyRecords
            .SingleAsync(item => item.Scope == scope && item.Key == key);
        record.Status.Should().Be(IdempotencyRecordStatus.Completed);
        record.AttemptCount.Should().Be(1);
    }

    [PostgreSqlFact]
    public async Task Expired_worker_cannot_commit_business_changes_after_claim_is_reacquired()
    {
        _fixture.EnsureEnabled();
        var tenantId = $"idempotency-expired-worker-{Guid.NewGuid():N}";
        var scope = $"POST:/stock/{Guid.NewGuid():N}";
        const string key = "expired-worker-request";
        const string hash = "expired-worker-hash";
        var operationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using (var setup = _fixture.CreateContext(tenantId))
        {
            setup.IdempotencyRecords.Add(new IdempotencyRecord
            {
                TenantId = tenantId,
                Scope = scope,
                Key = key,
                RequestHash = hash,
                Status = IdempotencyRecordStatus.Failed,
                AttemptCount = 1,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            });
            await setup.SaveChangesAsync();
        }

        await using var firstContext = _fixture.CreateContext(tenantId, "expired-worker-first");
        var firstStore = new IdempotencyKeyStore(
            firstContext,
            new TestTenantContext(tenantId),
            new UnitOfWork(firstContext));

        var first = firstStore.ExecuteAsync(scope, key, hash, async () =>
        {
            firstContext.Items.Add(new Item
            {
                ItemCode = "EXPIRED-WORKER-ITEM",
                Description = "must roll back when its claim is fenced",
                Rate = 1m
            });
            await firstContext.SaveChangesAsync();
            operationStarted.SetResult();
            await releaseFirstOperation.Task;
        });

        await operationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using (var expire = _fixture.CreateContext(tenantId, "expired-worker-expirer"))
        {
            var record = await expire.IdempotencyRecords.SingleAsync(item => item.Scope == scope && item.Key == key);
            record.LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1);
            await expire.SaveChangesAsync();
        }

        await using (var secondContext = _fixture.CreateContext(tenantId, "expired-worker-reclaimer"))
        {
            var secondStore = new IdempotencyKeyStore(
                secondContext,
                new TestTenantContext(tenantId),
                new UnitOfWork(secondContext));
            await secondStore.ExecuteAsync(scope, key, hash, () =>
            {
                secondContext.Items.Add(new Item
                {
                    ItemCode = "RECLAIMED-WORKER-ITEM",
                    Description = "committed by the current claim owner",
                    Rate = 1m
                });
                return Task.CompletedTask;
            });
        }

        releaseFirstOperation.SetResult();
        await first;

        await using var verify = _fixture.CreateContext(tenantId);
        (await verify.Items.CountAsync(item => item.ItemCode == "EXPIRED-WORKER-ITEM")).Should().Be(0);
        (await verify.Items.CountAsync(item => item.ItemCode == "RECLAIMED-WORKER-ITEM")).Should().Be(1);
        var completed = await verify.IdempotencyRecords.SingleAsync(item => item.Scope == scope && item.Key == key);
        completed.Status.Should().Be(IdempotencyRecordStatus.Completed);
        completed.AttemptCount.Should().Be(3);
    }
}
