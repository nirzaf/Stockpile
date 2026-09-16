using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Services;
using Merconiq.Infrastructure.Data;
using Merconiq.Infrastructure.Repositories;
using Merconiq.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;

namespace Merconiq.Tests.Integration;

public sealed class StockValuationIntegrationTests
{
    [Fact]
    public async Task Costed_receipts_and_sale_use_weighted_average_cost()
    {
        var tenantId = $"valuation-{Guid.NewGuid():N}";
        var databaseName = Guid.NewGuid().ToString();
        int itemId;
        int locationId;
        await using (var setup = CreateInMemoryContext(databaseName, tenantId))
        {
            (itemId, locationId) = await SeedItemAndLocationAsync(setup);
        }

        await using (var first = CreateInMemoryContext(databaseName, tenantId))
        {
            await CreateService(first, tenantId).ReceiveStockAsync(itemId, locationId, 10, null, unitCost: 10m);
        }
        await using (var second = CreateInMemoryContext(databaseName, tenantId))
        {
            await CreateService(second, tenantId).ReceiveStockAsync(itemId, locationId, 10, null, unitCost: 14m);
        }
        await using (var sale = CreateInMemoryContext(databaseName, tenantId))
        {
            await CreateService(sale, tenantId).SellStockAsync(itemId, locationId, 5, null);
        }

        await using var context = CreateInMemoryContext(databaseName, tenantId);
        var bucket = await context.StockValuationBuckets.SingleAsync();
        bucket.Quantity.Should().Be(15);
        bucket.Value.Should().Be(180m);

        var entries = await context.StockValuationEntries.OrderBy(entry => entry.Id).ToListAsync();
        entries.Should().HaveCount(3);
        entries[0].TotalValue.Should().Be(100m);
        entries[1].TotalValue.Should().Be(140m);
        entries[2].EntryType.Should().Be(StockValuationEntryType.Sale);
        entries[2].UnitCost.Should().Be(12m);
        entries[2].TotalValue.Should().Be(60m);
        entries.All(entry => entry.StockTransactionId > 0).Should().BeTrue();
    }

    [Fact]
    public async Task Final_valued_units_consume_the_exact_remaining_value()
    {
        var tenantId = $"valuation-final-{Guid.NewGuid():N}";
        var databaseName = Guid.NewGuid().ToString();
        int itemId;
        int locationId;
        await using (var setup = CreateInMemoryContext(databaseName, tenantId))
        {
            (itemId, locationId) = await SeedItemAndLocationAsync(setup);
        }

        await using (var receive = CreateInMemoryContext(databaseName, tenantId))
        {
            await CreateService(receive, tenantId).ReceiveStockAsync(itemId, locationId, 3, null, unitCost: 0.333333m);
        }
        await using (var sale = CreateInMemoryContext(databaseName, tenantId))
        {
            await CreateService(sale, tenantId).SellStockAsync(itemId, locationId, 3, null);
        }

        await using var context = CreateInMemoryContext(databaseName, tenantId);
        var bucket = await context.StockValuationBuckets.SingleAsync();
        bucket.Quantity.Should().Be(0);
        bucket.Value.Should().Be(0m);
        (await context.StockValuationEntries.SingleAsync(entry => entry.EntryType == StockValuationEntryType.Sale))
            .TotalValue.Should().Be(0.999999m);
    }

    [Fact]
    public async Task Valuation_entries_are_append_only()
    {
        var tenantId = $"valuation-immutable-{Guid.NewGuid():N}";
        var databaseName = Guid.NewGuid().ToString();
        int itemId;
        int locationId;
        await using (var setup = CreateInMemoryContext(databaseName, tenantId))
        {
            (itemId, locationId) = await SeedItemAndLocationAsync(setup);
        }

        await using (var receive = CreateInMemoryContext(databaseName, tenantId))
        {
            await CreateService(receive, tenantId).ReceiveStockAsync(itemId, locationId, 1, null, unitCost: 10m);
        }

        await using (var update = CreateInMemoryContext(databaseName, tenantId))
        {
            var entry = await update.StockValuationEntries.SingleAsync();
            entry.TotalValue = 11m;
            var act = () => update.SaveChangesAsync();
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Stock valuation entries are append-only.");
        }

        await using (var delete = CreateInMemoryContext(databaseName, tenantId))
        {
            var entry = await delete.StockValuationEntries.SingleAsync();
            delete.StockValuationEntries.Remove(entry);
            var act = () => delete.SaveChangesAsync();
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Stock valuation entries are append-only.");
        }
    }

    [Fact]
    public async Task Costed_receipt_rejects_lot_scope_without_writing()
    {
        var tenantId = $"valuation-reject-{Guid.NewGuid():N}";
        await using var context = CreateInMemoryContext(Guid.NewGuid().ToString(), tenantId);
        var (itemId, locationId) = await SeedItemAndLocationAsync(context);
        var service = CreateService(context, tenantId);

        var act = () => service.ReceiveStockAsync(
            itemId, locationId, 2, null, "LOT-1", unitCost: 10m);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Valuation is scoped to unbatched stock.");
        (await context.StockInHand.CountAsync()).Should().Be(0);
        (await context.StockTransactions.CountAsync()).Should().Be(0);
        (await context.StockValuationBuckets.CountAsync()).Should().Be(0);
    }

    private static InventoryDbContext CreateInMemoryContext(string databaseName, string tenantId)
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
        return new InventoryDbContext(options, new TestTenantContext(tenantId));
    }

    private static async Task<(int ItemId, int LocationId)> SeedItemAndLocationAsync(InventoryDbContext context)
    {
        var item = new Item
        {
            ItemCode = $"VALUED-{Guid.NewGuid():N}",
            Description = "Valuation test item",
            Rate = 999m,
            ReorderLevel = 0
        };
        var location = new Location { Name = $"Valuation location {Guid.NewGuid():N}" };
        context.Items.Add(item);
        context.Locations.Add(location);
        await context.SaveChangesAsync();
        return (item.Id, location.Id);
    }

    private static StockService CreateService(InventoryDbContext context, string tenantId) => new(
        new Repository<StockInHand>(context),
        new Repository<StockTransaction>(context),
        new Repository<Item>(context),
        new Repository<Location>(context),
        new Repository<Branch>(context),
        new UnitOfWork(context),
        new Mock<IWebhookDispatcher>().Object,
        new TestTenantContext(tenantId),
        NullLogger<StockService>.Instance,
        new Repository<StockValuationBucket>(context),
        new Repository<StockValuationEntry>(context));
}

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class StockValuationPostgreSqlIntegrationTests
{
    private readonly PostgreSqlIntegrationFixture _fixture;

    public StockValuationPostgreSqlIntegrationTests(PostgreSqlIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [PostgreSqlFact]
    public async Task PostgreSQL_costed_receipts_and_sale_preserve_exact_weighted_average_values()
    {
        _fixture.EnsureEnabled();
        var tenantId = $"valuation-weighted-{Guid.NewGuid():N}";
        var (itemId, locationId) = await SeedItemAndLocationAsync(tenantId);

        await using (var first = _fixture.CreateContext(tenantId))
        {
            await CreateService(first, tenantId).ReceiveStockAsync(itemId, locationId, 10, null, unitCost: 10m);
        }
        await using (var second = _fixture.CreateContext(tenantId))
        {
            await CreateService(second, tenantId).ReceiveStockAsync(itemId, locationId, 10, null, unitCost: 14m);
        }
        await using (var sale = _fixture.CreateContext(tenantId))
        {
            await CreateService(sale, tenantId).SellStockAsync(itemId, locationId, 5, null);
        }

        await using var verify = _fixture.CreateContext(tenantId);
        var bucket = await verify.StockValuationBuckets.SingleAsync();
        bucket.Quantity.Should().Be(15);
        bucket.Value.Should().Be(180m);

        var saleEntry = await verify.StockValuationEntries
            .SingleAsync(entry => entry.EntryType == StockValuationEntryType.Sale);
        saleEntry.UnitCost.Should().Be(12m);
        saleEntry.TotalValue.Should().Be(60m);
        (await verify.StockValuationEntries.CountAsync()).Should().Be(3);
    }

    [PostgreSqlFact]
    public async Task PostgreSQL_final_valued_units_consume_exact_remaining_value()
    {
        _fixture.EnsureEnabled();
        var tenantId = $"valuation-final-{Guid.NewGuid():N}";
        var (itemId, locationId) = await SeedItemAndLocationAsync(tenantId);

        await using (var receive = _fixture.CreateContext(tenantId))
        {
            await CreateService(receive, tenantId)
                .ReceiveStockAsync(itemId, locationId, 3, null, unitCost: 0.333333m);
        }
        await using (var sale = _fixture.CreateContext(tenantId))
        {
            await CreateService(sale, tenantId).SellStockAsync(itemId, locationId, 3, null);
        }

        await using var verify = _fixture.CreateContext(tenantId);
        var bucket = await verify.StockValuationBuckets.SingleAsync();
        bucket.Quantity.Should().Be(0);
        bucket.Value.Should().Be(0m);
        (await verify.StockValuationEntries.SingleAsync(entry => entry.EntryType == StockValuationEntryType.Sale))
            .TotalValue.Should().Be(0.999999m);
    }

    [PostgreSqlFact]
    public async Task PostgreSQL_failed_unvalued_sale_rolls_back_stock_and_valuation_changes()
    {
        _fixture.EnsureEnabled();
        var tenantId = $"valuation-rollback-{Guid.NewGuid():N}";
        var (itemId, locationId) = await SeedItemAndLocationAsync(tenantId);

        await using (var valuedReceive = _fixture.CreateContext(tenantId))
        {
            await CreateService(valuedReceive, tenantId)
                .ReceiveStockAsync(itemId, locationId, 5, null, unitCost: 10m);
        }
        await using (var unvaluedReceive = _fixture.CreateContext(tenantId))
        {
            await CreateService(unvaluedReceive, tenantId)
                .ReceiveStockAsync(itemId, locationId, 5, null);
        }

        await using (var sale = _fixture.CreateContext(tenantId))
        {
            var act = () => CreateService(sale, tenantId).SellStockAsync(itemId, locationId, 6, null);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Valued stock is insufficient for sale.");
        }

        await using var verify = _fixture.CreateContext(tenantId);
        (await verify.StockInHand.SingleAsync()).Quantity.Should().Be(10);
        var bucket = await verify.StockValuationBuckets.SingleAsync();
        bucket.Quantity.Should().Be(5);
        bucket.Value.Should().Be(50m);
        (await verify.StockTransactions.CountAsync()).Should().Be(2);
        (await verify.StockValuationEntries.CountAsync()).Should().Be(1);
    }

    [PostgreSqlFact]
    public async Task PostgreSQL_concurrent_first_valuation_bucket_inserts_retry_to_one_bucket()
    {
        _fixture.EnsureEnabled();
        var tenantId = $"valuation-first-bucket-{Guid.NewGuid():N}";
        var (itemId, locationId) = await SeedItemAndLocationAsync(tenantId);
        await using (var setup = _fixture.CreateContext(tenantId))
        {
            setup.StockInHand.Add(new StockInHand { ItemId = itemId, LocationId = locationId });
            await setup.SaveChangesAsync();
        }

        var startGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = new[] { 10m, 14m }.Select(async unitCost =>
        {
            await startGate.Task;
            await using var context = _fixture.CreateContext(tenantId, $"valuation-first-{unitCost}");
            await CreateService(context, tenantId)
                .ReceiveStockAsync(itemId, locationId, 10, null, unitCost: unitCost);
        }).ToArray();

        startGate.SetResult(true);
        await Task.WhenAll(attempts);

        await using var verify = _fixture.CreateContext(tenantId);
        var bucket = await verify.StockValuationBuckets.SingleAsync();
        bucket.Quantity.Should().Be(20);
        bucket.Value.Should().Be(240m);
        (await verify.StockValuationEntries.CountAsync()).Should().Be(2);
        (await verify.StockInHand.SingleAsync()).Quantity.Should().Be(20);
    }

    [PostgreSqlFact]
    public async Task PostgreSQL_valuation_foreign_keys_include_tenant_identity()
    {
        _fixture.EnsureEnabled();
        var tenantA = $"valuation-fk-a-{Guid.NewGuid():N}";
        var tenantB = $"valuation-fk-b-{Guid.NewGuid():N}";
        var (itemA, locationA) = await SeedItemAndLocationAsync(tenantA);
        await using (var receive = _fixture.CreateContext(tenantA))
        {
            await CreateService(receive, tenantA).ReceiveStockAsync(itemA, locationA, 1, null, unitCost: 10m);
        }

        int locationB;
        await using (var setupB = _fixture.CreateContext(tenantB))
        {
            var location = new Location { Name = $"Valuation location {Guid.NewGuid():N}" };
            setupB.Locations.Add(location);
            await setupB.SaveChangesAsync();
            locationB = location.Id;
        }

        int sourceTransactionId;
        await using (var readA = _fixture.CreateContext(tenantA))
        {
            sourceTransactionId = await readA.StockTransactions.Select(transaction => transaction.Id).SingleAsync();
        }

        await using var forged = _fixture.CreateContext(tenantB);
        forged.StockValuationEntries.Add(new StockValuationEntry
        {
            StockTransactionId = sourceTransactionId,
            ItemId = itemA,
            LocationId = locationB,
            EntryType = StockValuationEntryType.Receipt,
            Quantity = 1,
            UnitCost = 10m,
            TotalValue = 10m
        });

        var act = () => forged.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [PostgreSqlFact]
    public async Task PostgreSQL_valuation_entries_are_append_only_at_the_database_boundary()
    {
        _fixture.EnsureEnabled();
        var tenantId = $"valuation-trigger-{Guid.NewGuid():N}";
        var (itemId, locationId) = await SeedItemAndLocationAsync(tenantId);
        await using (var receive = _fixture.CreateContext(tenantId))
        {
            await CreateService(receive, tenantId).ReceiveStockAsync(itemId, locationId, 1, null, unitCost: 10m);
        }

        await using var context = _fixture.CreateContext(tenantId);
        var entry = await context.StockValuationEntries.SingleAsync();
        var act = () => context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"StockValuationEntries\" SET \"TotalValue\" = 11 WHERE \"Id\" = {entry.Id}");
        await act.Should().ThrowAsync<PostgresException>()
            .Where(exception => exception.SqlState == PostgresErrorCodes.RaiseException);
    }

    [PostgreSqlFact]
    public async Task PostgreSQL_concurrent_valued_postings_retry_and_preserve_bucket()
    {
        _fixture.EnsureEnabled();
        var tenantId = $"valuation-concurrency-{Guid.NewGuid():N}";
        int itemId;
        int locationId;

        await using (var setup = _fixture.CreateContext(tenantId))
        {
            var item = new Item
            {
                ItemCode = $"VALUED-{Guid.NewGuid():N}",
                Description = "Concurrent valuation item",
                ReorderLevel = 0
            };
            var location = new Location { Name = $"Valuation location {Guid.NewGuid():N}" };
            setup.Items.Add(item);
            setup.Locations.Add(location);
            await setup.SaveChangesAsync();
            itemId = item.Id;
            locationId = location.Id;
        }

        await using (var initialContext = _fixture.CreateContext(tenantId))
        {
            await CreateService(initialContext, tenantId)
                .ReceiveStockAsync(itemId, locationId, 20, null, unitCost: 12m);
        }

        await using var firstContext = _fixture.CreateContext(tenantId, "valuation-first");
        await using var secondContext = _fixture.CreateContext(tenantId, "valuation-second");
        var first = CreateService(firstContext, tenantId);
        var second = CreateService(secondContext, tenantId);

        await Task.WhenAll(
            first.SellStockAsync(itemId, locationId, 5, "concurrent sale"),
            second.ReceiveStockAsync(itemId, locationId, 10, "concurrent receipt", unitCost: 14m));

        await using var verify = _fixture.CreateContext(tenantId);
        var bucket = await verify.StockValuationBuckets.SingleAsync();
        var sale = await verify.StockValuationEntries.SingleAsync(entry => entry.EntryType == StockValuationEntryType.Sale);

        bucket.Quantity.Should().Be(25);
        bucket.Value.Should().Be(380m - sale.TotalValue);
        sale.TotalValue.Should().BeGreaterThan(0m);
        (await verify.StockValuationEntries.CountAsync()).Should().Be(3);
        (await verify.StockInHand.SingleAsync(stock => stock.ItemId == itemId && stock.LocationId == locationId))
            .Quantity.Should().Be(25);
    }

    private static StockService CreateService(InventoryDbContext context, string tenantId) => new(
        new Repository<StockInHand>(context),
        new Repository<StockTransaction>(context),
        new Repository<Item>(context),
        new Repository<Location>(context),
        new Repository<Branch>(context),
        new UnitOfWork(context),
        new Mock<IWebhookDispatcher>().Object,
        new TestTenantContext(tenantId),
        NullLogger<StockService>.Instance,
        new Repository<StockValuationBucket>(context),
        new Repository<StockValuationEntry>(context));

    private async Task<(int ItemId, int LocationId)> SeedItemAndLocationAsync(string tenantId)
    {
        await using var setup = _fixture.CreateContext(tenantId);
        var item = new Item
        {
            ItemCode = $"VALUED-{Guid.NewGuid():N}",
            Description = "PostgreSQL valuation test item",
            ReorderLevel = 0
        };
        var location = new Location { Name = $"Valuation location {Guid.NewGuid():N}" };
        setup.Items.Add(item);
        setup.Locations.Add(location);
        await setup.SaveChangesAsync();
        return (item.Id, location.Id);
    }
}
