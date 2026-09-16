using FluentAssertions;
using InventoryManagementSystem.Core.Interfaces;
using InventoryManagementSystem.Infrastructure.Data;
using InventoryManagementSystem.Tests.Infrastructure;
using InventoryManagementSystem.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace InventoryManagementSystem.Tests.Integration;

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
        var firstStore = new IdempotencyKeyStore(firstContext, new TestTenantContext(tenantId));
        var secondStore = new IdempotencyKeyStore(secondContext, new TestTenantContext(tenantId));

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
}
