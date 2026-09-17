using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Services;
using Merconiq.Infrastructure.Data;
using Merconiq.Infrastructure.Repositories;
using Merconiq.Tests.Infrastructure;
using Merconiq.Web.Controllers.Api.V1;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class TaxRulesPostgreSqlIntegrationTests(PostgreSqlIntegrationFixture fixture)
{
    [PostgreSqlFact]
    public async Task Concurrent_overlapping_periods_for_the_same_code_allow_only_one_rule()
    {
        fixture.EnsureEnabled();
        var tenantId = $"tax-rule-overlap-{Guid.NewGuid():N}";

        async Task<IActionResult> CreateAsync(DateTime from, DateTime to, string appName)
        {
            await using var context = fixture.CreateContext(tenantId, appName);
            var controller = new TaxRulesController(
                new Repository<TaxRule>(context),
                new UnitOfWork(context));
            return await controller.Create(new TaxRuleRequest(
                "STANDARD",
                TaxCategory.Standard,
                15m,
                TaxCalculationMode.Exclusive,
                from,
                to));
        }

        await using var lockContext = fixture.CreateContext(tenantId, "tax-rule-lock-holder");
        var lockUnitOfWork = new UnitOfWork(lockContext);
        var lockAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lockHolder = lockUnitOfWork.ExecuteInTransactionAsync(async () =>
        {
            await lockUnitOfWork.AcquireTenantOperationLockAsync("tax-rule-period:STANDARD");
            lockAcquired.SetResult();
            await releaseLock.Task;
        });

        try
        {
            await lockAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var first = CreateAsync(
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                "tax-rule-first");
            var second = CreateAsync(
                new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                "tax-rule-second");

            var bothRequestsWaitedForLock = await WaitForAdvisoryLockWaitersAsync(
                fixture.ConnectionString,
                ["tax-rule-first", "tax-rule-second"]);
            releaseLock.TrySetResult();
            await lockHolder;
            var results = await Task.WhenAll(first, second);

            bothRequestsWaitedForLock.Should().BeTrue(
                "both requests must be observed waiting on the PostgreSQL advisory lock before it is released");
            results.OfType<CreatedAtActionResult>().Should().HaveCount(1);
            results.OfType<BadRequestObjectResult>().Should().HaveCount(1);
        }
        finally
        {
            releaseLock.TrySetResult();
            await lockHolder;
        }

        await using var verify = fixture.CreateContext(tenantId);
        (await verify.TaxRules.CountAsync()).Should().Be(1);
    }

    private static async Task<bool> WaitForAdvisoryLockWaitersAsync(
        string connectionString,
        string[] applicationNames)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT COUNT(*)
                FROM pg_stat_activity
                WHERE application_name = ANY(@application_names)
                  AND wait_event_type = 'Lock'
                  AND wait_event = 'advisory'
                """,
                connection);
            command.Parameters.AddWithValue("application_names", applicationNames);
            var waiting = (long)(await command.ExecuteScalarAsync())!;
            if (waiting >= applicationNames.Length)
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }
}
