using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Exceptions;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Core.Services;
using Merconiq.Infrastructure.Data;
using Merconiq.Infrastructure.Repositories;
using Merconiq.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class StockReturnPostgreSqlIntegrationTests(PostgreSqlIntegrationFixture fixture)
{
    [PostgreSqlFact]
    public async Task Source_linked_returns_are_idempotent_limited_and_valued_by_the_original_sale()
    {
        fixture.EnsureEnabled();
        var tenant = $"return-{Guid.NewGuid():N}";
        int itemId;
        int locationId;
        int saleId;

        await using (var setup = fixture.CreateContext(tenant))
        {
            var item = new Item { ItemCode = $"RET-{Guid.NewGuid():N}", Description = "Returns" };
            var location = new Location { Name = $"Returns {Guid.NewGuid():N}" };
            setup.Items.Add(item);
            setup.Locations.Add(location);
            await setup.SaveChangesAsync();
            itemId = item.Id;
            locationId = location.Id;

        }

        await using (var context = fixture.CreateContext(tenant))
        {
            await CreateService(context, tenant).ReceiveStockAsync(
                itemId, locationId, 10, "receipt", unitCost: 5m);
        }

        await using (var context = fixture.CreateContext(tenant))
        {
            await CreateService(context, tenant).SellStockAsync(itemId, locationId, 4, "sale");
            saleId = await context.StockTransactions
                .Where(transaction => transaction.TransactionType == TransactionType.Sell)
                .Select(transaction => transaction.Id)
                .SingleAsync();
        }

        await using (var context = fixture.CreateContext(tenant))
        {
            await CreateService(context, tenant).ReturnStockAsync(
                new CreateStockReturnRequest(saleId, 1, StockReturnDisposition.Restockable, "return-line-1"));
        }

        await using (var context = fixture.CreateContext(tenant))
        {
            await CreateService(context, tenant).ReturnStockAsync(
                new CreateStockReturnRequest(saleId, 1, StockReturnDisposition.Restockable, "return-line-1"));
        }

        await using (var context = fixture.CreateContext(tenant))
        {
            await CreateService(context, tenant).ReturnStockAsync(
                new CreateStockReturnRequest(saleId, 2, StockReturnDisposition.Damaged, "return-line-2"));
        }

        await using (var context = fixture.CreateContext(tenant))
        {
            var differentReplay = () => CreateService(context, tenant).ReturnStockAsync(
                new CreateStockReturnRequest(saleId, 2, StockReturnDisposition.Restockable, "return-line-1"));
            await differentReplay.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("The source line already has a different return.");
        }

        await using (var context = fixture.CreateContext(tenant))
        {
            var overReturn = () => CreateService(context, tenant).ReturnStockAsync(
                new CreateStockReturnRequest(saleId, 2, StockReturnDisposition.Quarantined, "return-line-3"));
            await overReturn.Should().ThrowAsync<StockAvailabilityConflictException>()
                .WithMessage("Return quantity exceeds the eligible quantity of 1.");
        }

        await using (var verify = fixture.CreateContext(tenant))
        {
            var stock = await verify.StockInHand.SingleAsync();
            stock.ItemId.Should().Be(itemId);
            stock.LocationId.Should().Be(locationId);
            stock.Quantity.Should().Be(9);
            stock.ReservedQuantity.Should().Be(0);
            stock.QuarantinedQuantity.Should().Be(2);

            var transactions = await verify.StockTransactions.OrderBy(transaction => transaction.Id).ToListAsync();
            transactions.Should().HaveCount(4);
            transactions.Select(transaction => transaction.TransactionType)
                .Should().Equal(TransactionType.Receive, TransactionType.Sell, TransactionType.Return, TransactionType.Return);
            var returns = transactions.Where(transaction => transaction.TransactionType == TransactionType.Return).ToList();
            returns.Should().HaveCount(2);
            returns.Should().ContainSingle(transaction =>
                transaction.OriginalTransactionId == saleId &&
                transaction.Quantity == 1 &&
                transaction.SourceLineReference == "return-line-1" &&
                transaction.ReturnDisposition == StockReturnDisposition.Restockable &&
                transaction.UnitCost == 5m);
            returns.Should().ContainSingle(transaction =>
                transaction.OriginalTransactionId == saleId &&
                transaction.Quantity == 2 &&
                transaction.SourceLineReference == "return-line-2" &&
                transaction.ReturnDisposition == StockReturnDisposition.Damaged &&
                transaction.UnitCost == 5m);

            var bucket = await verify.StockValuationBuckets.SingleAsync();
            bucket.Quantity.Should().Be(9);
            bucket.Value.Should().Be(45m);
            var entries = await verify.StockValuationEntries.OrderBy(entry => entry.Id).ToListAsync();
            entries.Select(entry => entry.EntryType)
                .Should().Equal(StockValuationEntryType.Receipt, StockValuationEntryType.Sale,
                    StockValuationEntryType.Return, StockValuationEntryType.Return);
            entries.Where(entry => entry.EntryType == StockValuationEntryType.Return)
                .Select(entry => new { entry.Quantity, entry.UnitCost, entry.TotalValue })
                .Should().BeEquivalentTo(
                    [new { Quantity = 1, UnitCost = 5m, TotalValue = 5m },
                     new { Quantity = 2, UnitCost = 5m, TotalValue = 10m }]);
        }

        await using (var context = fixture.CreateContext(tenant))
        {
            var transaction = await context.StockTransactions
                .SingleAsync(row => row.SourceLineReference == "return-line-1");
            transaction.Notes = "forbidden edit";
            var update = () => context.SaveChangesAsync();
            await update.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Stock transactions are append-only and cannot be updated or deleted.");

            context.ChangeTracker.Clear();
            transaction = await context.StockTransactions
                .SingleAsync(row => row.SourceLineReference == "return-line-1");
            context.StockTransactions.Remove(transaction);
            var delete = () => context.SaveChangesAsync();
            await delete.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Stock transactions are append-only and cannot be updated or deleted.");
        }

        await using (var context = fixture.CreateContext(tenant))
        {
            var directUpdate = () => context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"StockTransactions\" SET \"Notes\" = {"forbidden"} WHERE \"Id\" = {saleId}");
            await directUpdate.Should().ThrowAsync<PostgresException>()
                .Where(exception => exception.SqlState == "55000");
        }
    }

    private static StockService CreateService(InventoryDbContext context, string tenant) => new(
        new Repository<StockInHand>(context),
        new Repository<StockTransaction>(context),
        new Repository<Item>(context),
        new Repository<Location>(context),
        new Repository<Branch>(context),
        new UnitOfWork(context),
        new Mock<IWebhookDispatcher>().Object,
        new TestTenantContext(tenant),
        NullLogger<StockService>.Instance,
        new Repository<StockValuationBucket>(context),
        new Repository<StockValuationEntry>(context),
        new Repository<StockReservation>(context));
}

public sealed class StockReturnApiTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    [Fact]
    public async Task Return_api_posts_against_a_sale_and_replays_the_same_source_line()
    {
        int saleId;
        int itemId;
        int locationId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            var company = new Company { Code = $"RT-{Guid.NewGuid():N}"[..10], LegalName = "Return company" };
            db.Companies.Add(company);
            await db.SaveChangesAsync();
            var branch = new Branch { CompanyId = company.Id, Code = "RET-BR", Name = "Return branch" };
            db.Branches.Add(branch);
            await db.SaveChangesAsync();
            var item = new Item { ItemCode = $"RTA-{Guid.NewGuid():N}"[..15], Description = "Return API" };
            var location = new Location { Name = "Return API location", BranchId = branch.Id };
            db.Items.Add(item);
            db.Locations.Add(location);
            await db.SaveChangesAsync();
            db.StockInHand.Add(new StockInHand { ItemId = item.Id, LocationId = location.Id, Quantity = 4 });
            var sale = new StockTransaction
            {
                ItemId = item.Id,
                FromLocationId = location.Id,
                Quantity = 2,
                TransactionType = TransactionType.Sell,
                TransactionDate = DateTime.UtcNow
            };
            db.StockTransactions.Add(sale);
            await db.SaveChangesAsync();
            saleId = sale.Id;
            itemId = item.Id;
            locationId = location.Id;
        }

        using var client = factory.CreateAuthenticatedClient();
        var request = new CreateStockReturnRequest(
            saleId, 1, StockReturnDisposition.Restockable, $"api-return-{Guid.NewGuid():N}");
        var first = await client.PostAsJsonAsync("/api/v1/stock/returns", request);
        var second = await client.PostAsJsonAsync("/api/v1/stock/returns", request);

        first.StatusCode.Should().Be(HttpStatusCode.NoContent, await first.Content.ReadAsStringAsync());
        second.StatusCode.Should().Be(HttpStatusCode.NoContent, await second.Content.ReadAsStringAsync());
        using var verifyScope = factory.Services.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        (await verify.StockTransactions.CountAsync(transaction =>
            transaction.TransactionType == TransactionType.Return && transaction.OriginalTransactionId == saleId))
            .Should().Be(1);
        (await verify.StockInHand.SingleAsync(stock => stock.ItemId == itemId && stock.LocationId == locationId))
            .Quantity.Should().Be(5);
    }
}
