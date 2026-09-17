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
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<IActionResult> CreateAsync(DateTime from, DateTime to, string appName)
        {
            await startGate.Task;
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

        var first = CreateAsync(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            "tax-rule-first");
        var second = CreateAsync(
            new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            "tax-rule-second");
        startGate.SetResult();

        var results = await Task.WhenAll(first, second);

        results.OfType<CreatedAtActionResult>().Should().HaveCount(1);
        results.OfType<BadRequestObjectResult>().Should().HaveCount(1);
        await using var verify = fixture.CreateContext(tenantId);
        (await verify.TaxRules.CountAsync()).Should().Be(1);
    }
}
