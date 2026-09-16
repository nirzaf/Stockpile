using FluentAssertions;
using System.Collections.Concurrent;
using System.Data.Common;
using Merconiq.Core.Entities;
using Merconiq.Core.Exceptions;
using Merconiq.Core.Options;
using Merconiq.Core.Services;
using Merconiq.Infrastructure.Repositories;
using Merconiq.Tests.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class ForecastResourceLimitsPostgreSqlIntegrationTests
{
    private readonly PostgreSqlIntegrationFixture _fixture;

    public ForecastResourceLimitsPostgreSqlIntegrationTests(PostgreSqlIntegrationFixture fixture) =>
        _fixture = fixture;

    [PostgreSqlFact]
    public async Task Per_item_limit_is_applied_after_company_filter_in_postgresql()
    {
        _fixture.EnsureEnabled();
        var tenantId = Unique("forecast-company-scope");
        int itemId;
        int companyAId;
        int companyBId;

        await using (var setup = _fixture.CreateContext(tenantId))
        {
            var companyA = new Company { Code = Unique("COA"), LegalName = "Forecast company A" };
            var companyB = new Company { Code = Unique("COB"), LegalName = "Forecast company B" };
            setup.Companies.AddRange(companyA, companyB);
            await setup.SaveChangesAsync();

            var branchA = new Branch { CompanyId = companyA.Id, Code = Unique("BRA"), Name = "Branch A" };
            var branchB = new Branch { CompanyId = companyB.Id, Code = Unique("BRB"), Name = "Branch B" };
            setup.Branches.AddRange(branchA, branchB);
            await setup.SaveChangesAsync();

            var locationA = new Location { Name = Unique("loc-a"), BranchId = branchA.Id };
            var locationB = new Location { Name = Unique("loc-b"), BranchId = branchB.Id };
            var item = new Item { ItemCode = Unique("FC-ITEM"), Description = "Forecast row-cap fixture", Rate = 1m };
            var companyAOnlyItem = new Item
            {
                ItemCode = Unique("FC-A-ONLY"),
                Description = "Company A-only item for scoped all-item cap",
                Rate = 1m
            };
            setup.Locations.AddRange(locationA, locationB);
            setup.Items.AddRange(item, companyAOnlyItem);
            await setup.SaveChangesAsync();

            var today = DateTime.UtcNow.Date;
            setup.StockTransactions.AddRange(
                Enumerable.Range(1, 3).Select(day => new StockTransaction
                {
                    ItemId = item.Id,
                    FromLocationId = locationA.Id,
                    TransactionType = TransactionType.Sell,
                    TransactionDate = today.AddDays(-day),
                    Quantity = 2
                }));
            setup.StockTransactions.AddRange(
                Enumerable.Range(1, 2).Select(day => new StockTransaction
                {
                    ItemId = item.Id,
                    FromLocationId = locationB.Id,
                    TransactionType = TransactionType.Sell,
                    TransactionDate = today.AddDays(-day),
                    Quantity = 9
                }));
            setup.StockTransactions.Add(new StockTransaction
            {
                ItemId = companyAOnlyItem.Id,
                FromLocationId = locationA.Id,
                TransactionType = TransactionType.Sell,
                TransactionDate = today.AddDays(-1),
                Quantity = 1
            });
            await setup.SaveChangesAsync();

            itemId = item.Id;
            companyAId = companyA.Id;
            companyBId = companyB.Id;
        }

        var sqlCapture = new SqlCommandCaptureInterceptor();
        await using var context = _fixture.CreateContext(tenantId, interceptors: [sqlCapture]);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(context, tenantId, cache, new ForecastingOptions
        {
            MaxHistoricalTransactionsPerForecast = 2,
            MaxItemsPerAllItemsForecast = 1
        });

        var companyBResult = await service.ForecastDemandForCompaniesAsync(itemId, 5, [companyBId]);
        companyBResult.ForecastedValues.Should().BeEmpty();
        sqlCapture.Commands.Should().Contain(command =>
            command.Text.Contains("\"StockTransactions\"", StringComparison.Ordinal) &&
            command.Text.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            command.Text.Contains("CompanyId", StringComparison.Ordinal) &&
            command.ParameterValues.Contains(3));

        var act = () => service.ForecastDemandForCompaniesAsync(itemId, 5, [companyAId]);
        var exception = await act.Should().ThrowAsync<ForecastResourceLimitExceededException>();
        exception.Which.Maximum.Should().Be(2);
        exception.Which.ObservedAtLeast.Should().Be(3);

        var companyBAllItems = await service.ForecastAllItemsForCompaniesAsync(5, [companyBId]);
        companyBAllItems.Should().BeEmpty();

        var companyAAllItems = () => service.ForecastAllItemsForCompaniesAsync(5, [companyAId]);
        var itemLimit = await companyAAllItems.Should().ThrowAsync<ForecastResourceLimitExceededException>();
        itemLimit.Which.Resource.Should().Be(nameof(ForecastingOptions.MaxItemsPerAllItemsForecast));
        itemLimit.Which.ObservedAtLeast.Should().Be(2);
    }

    [PostgreSqlFact]
    public async Task All_item_limit_counts_only_current_tenant_catalog_rows()
    {
        _fixture.EnsureEnabled();
        var tenantA = Unique("forecast-tenant-a");
        var tenantB = Unique("forecast-tenant-b");

        await using (var setupA = _fixture.CreateContext(tenantA))
        {
            setupA.Items.AddRange(Enumerable.Range(1, 2).Select(index => new Item
            {
                ItemCode = Unique($"TA-{index}"),
                Description = "Tenant A forecast cap fixture",
                Rate = 1m
            }));
            await setupA.SaveChangesAsync();
        }

        await using (var setupB = _fixture.CreateContext(tenantB))
        {
            setupB.Items.AddRange(Enumerable.Range(1, 5).Select(index => new Item
            {
                ItemCode = Unique($"TB-{index}"),
                Description = "Tenant B forecast cap fixture",
                Rate = 1m
            }));
            await setupB.SaveChangesAsync();
        }

        var options = new ForecastingOptions { MaxItemsPerAllItemsForecast = 2 };
        var tenantSqlCapture = new SqlCommandCaptureInterceptor();
        await using (var contextA = _fixture.CreateContext(tenantA, interceptors: [tenantSqlCapture]))
        using (var cacheA = new MemoryCache(new MemoryCacheOptions()))
        {
            var tenantAResult = await CreateService(contextA, tenantA, cacheA, options).ForecastAllItemsAsync(5);
            tenantAResult.Should().BeEmpty();
            tenantSqlCapture.Commands.Should().Contain(command =>
                command.Text.Contains("\"Items\"", StringComparison.Ordinal) &&
                command.Text.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
                command.Text.Contains("TenantId", StringComparison.Ordinal) &&
                command.ParameterValues.Contains(3));
        }

        await using (var contextB = _fixture.CreateContext(tenantB))
        using (var cacheB = new MemoryCache(new MemoryCacheOptions()))
        {
            var act = () => CreateService(contextB, tenantB, cacheB, options).ForecastAllItemsAsync(5);
            var exception = await act.Should().ThrowAsync<ForecastResourceLimitExceededException>();
            exception.Which.Maximum.Should().Be(2);
            exception.Which.ObservedAtLeast.Should().Be(3);
        }
    }

    private static DemandForecastService CreateService(
        Merconiq.Infrastructure.Data.InventoryDbContext context,
        string tenantId,
        IMemoryCache cache,
        ForecastingOptions options) =>
        new(
            new Repository<StockTransaction>(context),
            new Repository<Item>(context),
            NullLogger<DemandForecastService>.Instance,
            cache,
            new TestTenantContext(tenantId),
            Options.Create(options));

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 9, 64)];

    private sealed class SqlCommandCaptureInterceptor : DbCommandInterceptor
    {
        private readonly ConcurrentBag<CapturedCommand> _commands = [];

        public IReadOnlyCollection<CapturedCommand> Commands => _commands.ToArray();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _commands.Add(new CapturedCommand(
                command.CommandText,
                command.Parameters.Cast<DbParameter>().Select(parameter => parameter.Value).ToArray()));
            return ValueTask.FromResult(result);
        }
    }

    private sealed record CapturedCommand(string Text, object?[] ParameterValues);
}
