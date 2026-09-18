using System.Linq.Expressions;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
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
    public async Task Costed_lot_receipts_feed_the_shared_item_location_moving_average_bucket()
    {
        var tenantId = $"valuation-lots-{Guid.NewGuid():N}";
        var databaseName = Guid.NewGuid().ToString();
        await using var context = CreateInMemoryContext(databaseName, tenantId);
        var (itemId, locationId) = await SeedItemAndLocationAsync(context);
        var service = CreateService(context, tenantId);
        var firstExpiry = DateTime.UtcNow.Date.AddMonths(6);
        var secondExpiry = DateTime.UtcNow.Date.AddYears(1);

        await service.ReceiveStockAsync(
            itemId, locationId, 10, null, "LOT-1", firstExpiry, unitCost: 10m);
        await service.ReceiveStockAsync(
            itemId, locationId, 10, null, "LOT-2", secondExpiry, unitCost: 20m);

        var bucket = await context.StockValuationBuckets.SingleAsync();
        bucket.Quantity.Should().Be(20);
        bucket.Value.Should().Be(300m);
        var lots = await context.StockInHand.OrderBy(stock => stock.BatchNumber).ToListAsync();
        lots.Should().HaveCount(2);
        lots[0].Should().Match<StockInHand>(stock =>
            stock.BatchNumber == "LOT-1" && stock.Quantity == 10 && stock.ExpiryDate == firstExpiry);
        lots[1].Should().Match<StockInHand>(stock =>
            stock.BatchNumber == "LOT-2" && stock.Quantity == 10 && stock.ExpiryDate == secondExpiry);
        var entries = await context.StockValuationEntries
            .Include(entry => entry.StockTransaction)
            .OrderBy(entry => entry.Id)
            .ToListAsync();
        entries.Should().HaveCount(2);
        entries[0].TotalValue.Should().Be(100m);
        entries[0].StockTransaction.BatchNumber.Should().Be("LOT-1");
        entries[1].TotalValue.Should().Be(200m);
        entries[1].StockTransaction.BatchNumber.Should().Be("LOT-2");
    }

    [Fact]
    public async Task Unvalued_lot_receipts_transfers_and_returns_remain_unvalued()
    {
        var tenantId = $"valuation-unvalued-lot-{Guid.NewGuid():N}";
        var databaseName = Guid.NewGuid().ToString();
        int companyId;
        int itemId;
        int sourceLocationId;
        int destinationLocationId;
        var batchNumber = "LOT-UNVALUED";
        var expiryDate = DateTime.UtcNow.Date.AddMonths(6);

        await using (var setup = CreateInMemoryContext(databaseName, tenantId))
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var company = new Company { Code = $"UL-{suffix}", LegalName = "Unvalued lot company" };
            setup.Companies.Add(company);
            await setup.SaveChangesAsync();
            var sourceBranch = new Branch { CompanyId = company.Id, Code = $"ULS-{suffix}", Name = "Unvalued lot source" };
            var destinationBranch = new Branch { CompanyId = company.Id, Code = $"ULD-{suffix}", Name = "Unvalued lot destination" };
            setup.Branches.AddRange(sourceBranch, destinationBranch);
            await setup.SaveChangesAsync();
            var item = new Item
            {
                ItemCode = $"UL-ITEM-{suffix}",
                Description = "Unvalued lot transfer regression"
            };
            var source = new Location { Name = "Unvalued lot source location", BranchId = sourceBranch.Id };
            var destination = new Location { Name = "Unvalued lot destination location", BranchId = destinationBranch.Id };
            setup.Items.Add(item);
            setup.Locations.AddRange(source, destination);
            await setup.SaveChangesAsync();
            companyId = company.Id;
            itemId = item.Id;
            sourceLocationId = source.Id;
            destinationLocationId = destination.Id;
        }
        var mutationScope = new StockMutationScope(companyId);

        await using (var firstReceipt = CreateInMemoryContext(databaseName, tenantId))
            await CreateService(firstReceipt, tenantId).ReceiveStockAsync(
                itemId, sourceLocationId, 5, "first quantity-only receipt", batchNumber, expiryDate,
                mutationScope: mutationScope);
        await using (var secondReceipt = CreateInMemoryContext(databaseName, tenantId))
            await CreateService(secondReceipt, tenantId).ReceiveStockAsync(
                itemId, sourceLocationId, 3, "repeat quantity-only receipt", batchNumber, expiryDate,
                mutationScope: mutationScope);

        await using (var firstTransfer = CreateInMemoryContext(databaseName, tenantId))
            await CreateService(firstTransfer, tenantId).TransferStockAsync(
                itemId, sourceLocationId, destinationLocationId, 2, "first unvalued transfer", batchNumber, expiryDate,
                mutationScope);
        await using (var secondTransfer = CreateInMemoryContext(databaseName, tenantId))
            await CreateService(secondTransfer, tenantId).TransferStockAsync(
                itemId, sourceLocationId, destinationLocationId, 2, "repeat unvalued transfer", batchNumber, expiryDate,
                mutationScope);

        int saleId;
        await using (var sale = CreateInMemoryContext(databaseName, tenantId))
        {
            await CreateService(sale, tenantId).SellStockAsync(
                itemId, destinationLocationId, 1, "partial unvalued lot sale", batchNumber, expiryDate,
                mutationScope: mutationScope);
            saleId = await sale.StockTransactions
                .Where(transaction => transaction.TransactionType == TransactionType.Sell)
                .Select(transaction => transaction.Id)
                .SingleAsync();
        }

        await using (var stockReturn = CreateInMemoryContext(databaseName, tenantId))
            await CreateService(stockReturn, tenantId).ReturnStockAsync(
                new CreateStockReturnRequest(
                    saleId, 1, StockReturnDisposition.Restockable, "unvalued-lot-return"));

        await using var verify = CreateInMemoryContext(databaseName, tenantId);
        (await verify.StockInHand.SingleAsync(stock => stock.LocationId == sourceLocationId))
            .Should().Match<StockInHand>(stock =>
                stock.Quantity == 4 && stock.BatchNumber == batchNumber && stock.ExpiryDate == expiryDate);
        (await verify.StockInHand.SingleAsync(stock => stock.LocationId == destinationLocationId))
            .Should().Match<StockInHand>(stock =>
                stock.Quantity == 4 && stock.BatchNumber == batchNumber && stock.ExpiryDate == expiryDate);
        (await verify.StockValuationBuckets.CountAsync()).Should().Be(0);
        (await verify.StockValuationEntries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Valuation_entries_are_append_only_through_ef()
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
            entry.TotalValue = 99m;

            var act = () => update.SaveChangesAsync();

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Stock valuation entries are append-only and cannot be updated or deleted.");
        }

        await using var delete = CreateInMemoryContext(databaseName, tenantId);
        var persisted = await delete.StockValuationEntries.SingleAsync();
        delete.StockValuationEntries.Remove(persisted);

        var deleteAct = () => delete.SaveChangesAsync();

        await deleteAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Stock valuation entries are append-only and cannot be updated or deleted.");
    }

    [Fact]
    public async Task Sale_that_would_mix_valued_and_unvalued_quantity_is_rejected_atomically()
    {
        var tenantId = $"valuation-mixed-sale-{Guid.NewGuid():N}";
        var databaseName = Guid.NewGuid().ToString();
        int itemId;
        int locationId;
        await using (var setup = CreateInMemoryContext(databaseName, tenantId))
        {
            (itemId, locationId) = await SeedItemAndLocationAsync(setup);
        }

        await using (var valuedReceipt = CreateInMemoryContext(databaseName, tenantId))
        {
            await CreateService(valuedReceipt, tenantId).ReceiveStockAsync(itemId, locationId, 10, null, unitCost: 10m);
        }
        await using (var unvaluedReceipt = CreateInMemoryContext(databaseName, tenantId))
        {
            await CreateService(unvaluedReceipt, tenantId).ReceiveStockAsync(itemId, locationId, 5, null);
        }
        await using (var sale = CreateInMemoryContext(databaseName, tenantId))
        {
            var act = () => CreateService(sale, tenantId).SellStockAsync(itemId, locationId, 15, null);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Valued stock is insufficient for sale.");
        }

        await using var verify = CreateInMemoryContext(databaseName, tenantId);
        (await verify.StockInHand.SingleAsync()).Quantity.Should().Be(15);
        (await verify.StockTransactions.CountAsync()).Should().Be(2);
        (await verify.StockValuationBuckets.SingleAsync()).Quantity.Should().Be(10);
        (await verify.StockValuationEntries.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Quantity_only_sale_without_a_bucket_remains_unvalued()
    {
        var tenantId = $"valuation-unvalued-sale-{Guid.NewGuid():N}";
        var databaseName = Guid.NewGuid().ToString();
        int itemId;
        int locationId;
        await using (var setup = CreateInMemoryContext(databaseName, tenantId))
        {
            (itemId, locationId) = await SeedItemAndLocationAsync(setup);
        }

        await using (var receive = CreateInMemoryContext(databaseName, tenantId))
        {
            await CreateService(receive, tenantId).ReceiveStockAsync(itemId, locationId, 5, null);
        }
        await using (var sale = CreateInMemoryContext(databaseName, tenantId))
        {
            await CreateService(sale, tenantId).SellStockAsync(itemId, locationId, 2, null);
        }

        await using var verify = CreateInMemoryContext(databaseName, tenantId);
        (await verify.StockInHand.SingleAsync()).Quantity.Should().Be(3);
        (await verify.StockValuationBuckets.CountAsync()).Should().Be(0);
        (await verify.StockValuationEntries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task GetValuation_Returns_bucket_and_ordered_immutable_entries()
    {
        var tenantId = $"valuation-read-{Guid.NewGuid():N}";
        var databaseName = Guid.NewGuid().ToString();
        int itemId;
        int locationId;
        await using (var setup = CreateInMemoryContext(databaseName, tenantId))
        {
            (itemId, locationId) = await SeedItemAndLocationAsync(setup);
        }

        await using (var receive = CreateInMemoryContext(databaseName, tenantId))
        {
            await CreateService(receive, tenantId).ReceiveStockAsync(itemId, locationId, 10, null, unitCost: 10m);
        }
        await using (var sale = CreateInMemoryContext(databaseName, tenantId))
        {
            await CreateService(sale, tenantId).SellStockAsync(itemId, locationId, 5, null);
        }

        await using var context = CreateInMemoryContext(databaseName, tenantId);
        var result = (await CreateService(context, tenantId).GetValuationAsync(itemId, locationId)).Single();

        result.Quantity.Should().Be(5);
        result.Value.Should().Be(50m);
        result.Entries.Select(entry => entry.EntryType)
            .Should().Equal(StockValuationEntryType.Receipt, StockValuationEntryType.Sale);
        result.Entries.Select(entry => entry.StockTransactionId).Should().OnlyHaveUniqueItems();
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
    public async Task PostgreSQL_weighted_average_receipts_and_sale_preserve_expected_value()
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
        saleEntry.TotalValue.Should().Be(60m);
        saleEntry.UnitCost.Should().Be(12m);
    }

    [PostgreSqlFact]
    public async Task PostgreSQL_final_valued_sale_consumes_exact_remaining_value()
    {
        _fixture.EnsureEnabled();
        var tenantId = $"valuation-final-pg-{Guid.NewGuid():N}";
        var (itemId, locationId) = await SeedItemAndLocationAsync(tenantId);

        await using (var receive = _fixture.CreateContext(tenantId))
        {
            await CreateService(receive, tenantId).ReceiveStockAsync(itemId, locationId, 3, null, unitCost: 0.333333m);
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
    public async Task PostgreSQL_legacy_non_midnight_lot_expiry_retains_valuation_coverage()
    {
        _fixture.EnsureEnabled();
        var tenantId = $"valuation-legacy-expiry-{Guid.NewGuid():N}";
        var (itemId, locationId) = await SeedItemAndLocationAsync(tenantId);
        var expiryDay = DateTime.UtcNow.Date.AddMonths(6);
        var legacyExpiry = DateTime.SpecifyKind(expiryDay.AddHours(16), DateTimeKind.Utc);
        const string batchNumber = "LOT-LEGACY-VALUED";

        await using (var legacyBaseline = _fixture.CreateContext(tenantId))
        {
            var legacyReceipt = new StockTransaction
            {
                ItemId = itemId,
                FromLocationId = locationId,
                Quantity = 10,
                TransactionType = TransactionType.Receive,
                TransactionDate = DateTime.UtcNow,
                BatchNumber = batchNumber,
                ExpiryDate = legacyExpiry,
                UnitCost = 10m,
                Notes = "Legacy receipt expiry retained its time component"
            };
            legacyBaseline.StockInHand.Add(new StockInHand
            {
                ItemId = itemId,
                LocationId = locationId,
                Quantity = 10,
                BatchNumber = batchNumber,
                ExpiryDate = expiryDay
            });
            legacyBaseline.StockTransactions.Add(legacyReceipt);
            legacyBaseline.StockValuationBuckets.Add(new StockValuationBucket
            {
                ItemId = itemId,
                LocationId = locationId,
                Quantity = 10,
                Value = 100m
            });
            await legacyBaseline.SaveChangesAsync();
            legacyBaseline.StockValuationEntries.Add(new StockValuationEntry
            {
                StockTransactionId = legacyReceipt.Id,
                ItemId = itemId,
                LocationId = locationId,
                EntryType = StockValuationEntryType.Receipt,
                Quantity = 10,
                UnitCost = 10m,
                TotalValue = 100m
            });
            await legacyBaseline.SaveChangesAsync();
        }

        await using (var followupReceipt = _fixture.CreateContext(tenantId))
        {
            await CreateService(followupReceipt, tenantId).ReceiveStockAsync(
                itemId, locationId, 3, "follow-up valued lot receipt", batchNumber, expiryDay, unitCost: 20m);
        }

        await using var verify = _fixture.CreateContext(tenantId);
        var bucket = await verify.StockValuationBuckets.SingleAsync();
        bucket.Quantity.Should().Be(13);
        bucket.Value.Should().Be(160m);
        (await verify.StockInHand.SingleAsync(stock => stock.ItemId == itemId && stock.LocationId == locationId))
            .Should().Match<StockInHand>(stock => stock.Quantity == 13 && stock.ExpiryDate == expiryDay);
        (await verify.StockValuationEntries.OrderBy(entry => entry.Id).Select(entry => entry.TotalValue).ToListAsync())
            .Should().Equal(100m, 60m);
    }

    [PostgreSqlFact]
    public async Task PostgreSQL_failed_webhook_enqueue_rolls_back_stock_and_valuation_together()
    {
        _fixture.EnsureEnabled();
        var tenantId = $"valuation-rollback-{Guid.NewGuid():N}";
        var (itemId, locationId) = await SeedItemAndLocationAsync(tenantId);
        await using (var operation = _fixture.CreateContext(tenantId))
        {
            var service = CreateService(operation, tenantId, new ThrowingWebhookDispatcher());

            var act = () => service.ReceiveStockAsync(itemId, locationId, 5, null, unitCost: 10m);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Webhook enqueue failed.");
        }

        await using var verify = _fixture.CreateContext(tenantId);
        (await verify.StockInHand.CountAsync()).Should().Be(0);
        (await verify.StockTransactions.CountAsync()).Should().Be(0);
        (await verify.StockValuationBuckets.CountAsync()).Should().Be(0);
        (await verify.StockValuationEntries.CountAsync()).Should().Be(0);
    }

    [PostgreSqlFact]
    public async Task PostgreSQL_concurrent_first_costed_receipts_retry_the_bucket_insert_race()
    {
        _fixture.EnsureEnabled();
        var tenantId = $"valuation-first-race-{Guid.NewGuid():N}";
        var (itemId, locationId) = await SeedItemAndLocationAsync(tenantId);
        await using var firstContext = _fixture.CreateContext(tenantId, "valuation-first-race-a");
        await using var secondContext = _fixture.CreateContext(tenantId, "valuation-first-race-b");

        await Task.WhenAll(
            CreateService(firstContext, tenantId).ReceiveStockAsync(itemId, locationId, 10, "first", unitCost: 10m),
            CreateService(secondContext, tenantId).ReceiveStockAsync(itemId, locationId, 10, "second", unitCost: 14m));

        await using var verify = _fixture.CreateContext(tenantId);
        var bucket = await verify.StockValuationBuckets.SingleAsync();
        bucket.Quantity.Should().Be(20);
        bucket.Value.Should().Be(240m);
        (await verify.StockInHand.SingleAsync(stock => stock.ItemId == itemId && stock.LocationId == locationId))
            .Quantity.Should().Be(20);
        (await verify.StockValuationEntries.CountAsync()).Should().Be(2);
    }

    [PostgreSqlFact]
    public async Task PostgreSQL_valuation_entry_trigger_rejects_raw_updates_and_deletes()
    {
        _fixture.EnsureEnabled();
        var tenantId = $"valuation-trigger-{Guid.NewGuid():N}";
        var (itemId, locationId) = await SeedItemAndLocationAsync(tenantId);
        await using (var receive = _fixture.CreateContext(tenantId))
        {
            await CreateService(receive, tenantId).ReceiveStockAsync(itemId, locationId, 1, null, unitCost: 10m);
        }

        await using var update = _fixture.CreateContext(tenantId);
        var entryId = await update.StockValuationEntries.Select(entry => entry.Id).SingleAsync();
        var updateAct = () => update.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"StockValuationEntries\" SET \"TotalValue\" = 0 WHERE \"Id\" = {entryId}");
        await updateAct.Should().ThrowAsync<PostgresException>();

        await using var delete = _fixture.CreateContext(tenantId);
        var deleteAct = () => delete.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM \"StockValuationEntries\" WHERE \"Id\" = {entryId}");
        await deleteAct.Should().ThrowAsync<PostgresException>();
    }

    [PostgreSqlFact]
    public async Task PostgreSQL_valuation_foreign_keys_reject_cross_tenant_items_and_sources()
    {
        _fixture.EnsureEnabled();
        var tenantA = $"valuation-fk-a-{Guid.NewGuid():N}";
        var tenantB = $"valuation-fk-b-{Guid.NewGuid():N}";
        var (itemA, locationA) = await SeedItemAndLocationAsync(tenantA);
        int itemB;
        int locationB;
        int transactionB;
        await using (var setup = _fixture.CreateContext(tenantB))
        {
            (itemB, locationB) = await SeedItemAndLocationAsync(setup);
            var source = new StockTransaction
            {
                ItemId = itemB,
                FromLocationId = locationB,
                Quantity = 1,
                TransactionType = TransactionType.Receive,
                TransactionDate = DateTime.UtcNow
            };
            setup.StockTransactions.Add(source);
            await setup.SaveChangesAsync();
            transactionB = source.Id;
        }

        await using (var crossTenantItem = _fixture.CreateContext(tenantA))
        {
            crossTenantItem.StockValuationBuckets.Add(new StockValuationBucket
            {
                ItemId = itemB,
                LocationId = locationA,
                Quantity = 1,
                Value = 10m
            });
            var act = () => crossTenantItem.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>();
        }

        await using var crossTenantSource = _fixture.CreateContext(tenantA);
        crossTenantSource.StockValuationEntries.Add(new StockValuationEntry
        {
            StockTransactionId = transactionB,
            ItemId = itemA,
            LocationId = locationA,
            EntryType = StockValuationEntryType.Receipt,
            Quantity = 1,
            UnitCost = 10m,
            TotalValue = 10m
        });
        var sourceAct = () => crossTenantSource.SaveChangesAsync();
        await sourceAct.Should().ThrowAsync<DbUpdateException>();
    }

    [PostgreSqlFact]
    public async Task PostgreSQL_valuation_read_keeps_bucket_and_entries_on_one_snapshot_during_a_posting()
    {
        _fixture.EnsureEnabled();
        var tenantId = $"valuation-snapshot-{Guid.NewGuid():N}";
        var (itemId, locationId) = await SeedItemAndLocationAsync(tenantId);
        await using (var initial = _fixture.CreateContext(tenantId))
        {
            await CreateService(initial, tenantId)
                .ReceiveStockAsync(itemId, locationId, 10, null, unitCost: 10m);
        }

        await using var reader = _fixture.CreateContext(tenantId, "valuation-snapshot-reader");
        await using var writer = _fixture.CreateContext(tenantId, "valuation-snapshot-writer");
        var bucketRepository = new Mock<IRepository<StockValuationBucket>>();
        var innerBucketRepository = new Repository<StockValuationBucket>(reader);
        var postingCompleted = false;

        async Task<IEnumerable<StockValuationBucket>> QueryBucketsAndPostAsync(
            Expression<Func<StockValuationBucket, bool>> predicate)
        {
            var buckets = await innerBucketRepository.FindAsync(predicate);
            if (!postingCompleted)
            {
                postingCompleted = true;
                await CreateService(writer, tenantId)
                    .ReceiveStockAsync(itemId, locationId, 10, "concurrent receipt", unitCost: 14m);
            }

            return buckets;
        }

        bucketRepository
            .Setup(repository => repository.FindAsync(
                It.IsAny<Expression<Func<StockValuationBucket, bool>>>()))
            .Returns(QueryBucketsAndPostAsync);

        var valuation = (await CreateService(
            reader, tenantId, valuationBucketRepository: bucketRepository.Object)
            .GetValuationAsync(itemId, locationId)).Single();

        valuation.Quantity.Should().Be(10);
        valuation.Value.Should().Be(100m);
        valuation.Entries.Should().ContainSingle();
        valuation.Entries[0].TotalValue.Should().Be(100m);

        await using var verify = _fixture.CreateContext(tenantId);
        var currentBucket = await verify.StockValuationBuckets.SingleAsync();
        currentBucket.Quantity.Should().Be(20);
        currentBucket.Value.Should().Be(240m);
        (await verify.StockValuationEntries.CountAsync()).Should().Be(2);
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

    private async Task<(int ItemId, int LocationId)> SeedItemAndLocationAsync(string tenantId)
    {
        await using var context = _fixture.CreateContext(tenantId);
        var item = new Item
        {
            ItemCode = $"VALUED-{Guid.NewGuid():N}",
            Description = "PostgreSQL valuation test item",
            Rate = 999m,
            ReorderLevel = 0
        };
        var location = new Location { Name = $"Valuation location {Guid.NewGuid():N}" };
        context.Items.Add(item);
        context.Locations.Add(location);
        await context.SaveChangesAsync();
        return (item.Id, location.Id);
    }

    private static async Task<(int ItemId, int LocationId)> SeedItemAndLocationAsync(InventoryDbContext context)
    {
        var item = new Item
        {
            ItemCode = $"VALUED-{Guid.NewGuid():N}",
            Description = "PostgreSQL valuation test item",
            Rate = 999m,
            ReorderLevel = 0
        };
        var location = new Location { Name = $"Valuation location {Guid.NewGuid():N}" };
        context.Items.Add(item);
        context.Locations.Add(location);
        await context.SaveChangesAsync();
        return (item.Id, location.Id);
    }

    private static StockService CreateService(
        InventoryDbContext context,
        string tenantId,
        IWebhookDispatcher? webhookDispatcher = null,
        IRepository<StockValuationBucket>? valuationBucketRepository = null) => new(
        new Repository<StockInHand>(context),
        new Repository<StockTransaction>(context),
        new Repository<Item>(context),
        new Repository<Location>(context),
        new Repository<Branch>(context),
        new UnitOfWork(context),
        webhookDispatcher ?? new Mock<IWebhookDispatcher>().Object,
        new TestTenantContext(tenantId),
        NullLogger<StockService>.Instance,
        valuationBucketRepository ?? new Repository<StockValuationBucket>(context),
        new Repository<StockValuationEntry>(context));

    private sealed class ThrowingWebhookDispatcher : IWebhookDispatcher
    {
        public Task EnqueueAsync<T>(
            Merconiq.Core.Models.WebhookEvent<T> webhookEvent,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("Webhook enqueue failed."));

        public Task DispatchAsync<T>(Merconiq.Core.Models.WebhookEvent<T> webhookEvent) => Task.CompletedTask;
    }
}
