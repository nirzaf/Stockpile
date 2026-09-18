using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Merconiq.Infrastructure.Services;
using Merconiq.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Merconiq.Tests.Infrastructure;

public sealed class OpeningStockImportServiceTests
{
    [Fact]
    public async Task PreviewAsync_ValidatesRowsWithoutPersistingAndAcceptsExplicitZeroCost()
    {
        await using var context = CreateContext();
        context.Items.Add(new Item
        {
            TenantId = "test-tenant",
            ExternalId = "item-1",
            ItemCode = "SKU-1",
            Description = "Widget",
            IsActive = true
        });
        context.Locations.Add(new Location { TenantId = "test-tenant", Id = 7, Name = "Main" });
        await context.SaveChangesAsync();

        var result = await CreateService(context).PreviewAsync(new(
            "external_reference,item_external_id,location_id,quantity,unit_cost\nopen-1,item-1,7,10,0"));

        result.Valid.Should().Be(1);
        result.Rejected.Should().Be(0);
        result.Rows.Single().Status.Should().Be("valid");
        (await context.StockInHand.CountAsync()).Should().Be(0);
        (await context.StockTransactions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task PreviewAsync_RejectsDuplicatesMissingCostInvalidMastersAndQuantities()
    {
        await using var context = CreateContext();
        context.Items.Add(new Item
        {
            TenantId = "test-tenant",
            ExternalId = "item-1",
            ItemCode = "SKU-1",
            Description = "Widget",
            IsActive = true
        });
        context.Items.Add(new Item
        {
            TenantId = "test-tenant",
            ExternalId = "inactive-1",
            ItemCode = "SKU-2",
            Description = "Inactive",
            IsActive = false
        });
        context.Locations.Add(new Location { TenantId = "test-tenant", Id = 7, Name = "Main" });
        await context.SaveChangesAsync();

        var csv = "external_reference,item_external_id,location_id,quantity,unit_cost\n" +
                  "open-1,item-1,7,10,1.25\n" +
                  "open-1,item-1,7,10,1.25\n" +
                  "open-2,item-1,7,10,\n" +
                  "open-3,missing,7,10,1\n" +
                  "open-4,inactive-1,7,10,1\n" +
                  "open-5,item-1,99,10,1\n" +
                  "open-6,item-1,7,0,1";

        var result = await CreateService(context).PreviewAsync(new(csv));

        result.Valid.Should().Be(1);
        result.Rejected.Should().Be(6);
        result.Rows.Count(row => row.Status == "rejected").Should().Be(6);
        (await context.StockInHand.CountAsync()).Should().Be(0);
        (await context.StockTransactions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task PreviewAsync_DoesNotResolveMastersFromAnotherTenant()
    {
        var databaseName = Guid.NewGuid().ToString();
        await using (var otherTenantContext = CreateContext(databaseName, "other-tenant"))
        {
            otherTenantContext.Items.Add(new Item
            {
                ExternalId = "other-item",
                ItemCode = "OTHER-1",
                Description = "Other tenant item",
                IsActive = true
            });
            otherTenantContext.Locations.Add(new Location { Id = 8, Name = "Other" });
            await otherTenantContext.SaveChangesAsync();
        }

        await using var context = CreateContext(databaseName, "test-tenant");

        var result = await CreateService(context).PreviewAsync(new(
            "external_reference,item_external_id,location_id,quantity,unit_cost\nopen-1,other-item,8,10,1"));

        result.Valid.Should().Be(0);
        result.Rejected.Should().Be(1);
        result.Rows.Single().Error.Should().Contain("not found in the current tenant");
    }

    [Fact]
    public async Task PreviewAsync_ResolvesCaseVariantExternalIdsAsDistinctTenantItems()
    {
        await using var context = CreateContext();
        context.Items.AddRange(
            new Item
            {
                TenantId = "test-tenant",
                ExternalId = "Part-A",
                ItemCode = "PART-A-UPPER",
                Description = "Uppercase identifier",
                IsActive = true
            },
            new Item
            {
                TenantId = "test-tenant",
                ExternalId = "part-a",
                ItemCode = "PART-A-LOWER",
                Description = "Lowercase identifier",
                IsActive = true
            });
        context.Locations.Add(new Location { TenantId = "test-tenant", Id = 7, Name = "Main" });
        await context.SaveChangesAsync();

        var csv = "external_reference,item_external_id,location_id,quantity,unit_cost\n" +
                  "open-upper,Part-A,7,10,1\n" +
                  "open-lower,part-a,7,5,0\n" +
                  "open-nonmatch,PART-A,7,1,1";

        var result = await CreateService(context).PreviewAsync(new(csv));

        result.Valid.Should().Be(2);
        result.Rejected.Should().Be(1);
        result.Rows[0].Status.Should().Be("valid");
        result.Rows[1].Status.Should().Be("valid");
        result.Rows[2].Status.Should().Be("rejected");
        result.Rows[2].Error.Should().Contain("not found in the current tenant");
    }

    [Fact]
    public async Task ReplayAsync_AppliesOnceAndPersistsApprovedLineage()
    {
        await using var context = CreateContext();
        var item = new Item
        {
            ExternalId = "item-1",
            ItemCode = "SKU-1",
            Description = "Widget",
            IsActive = true
        };
        var location = new Location { Id = 7, Name = "Main" };
        context.Items.Add(item);
        context.Locations.Add(location);
        await context.SaveChangesAsync();

        var request = new OpeningStockReplayRequest(
            "external_reference,item_external_id,location_id,quantity,unit_cost\nopen-1,item-1,7,10,0",
            "import-1",
            "approval-1");

        var service = CreateService(context);
        var first = await service.ReplayAsync(request);

        first.AppliedRows.Should().Be(1);
        first.AlreadyApplied.Should().BeFalse();
        (await context.StockInHand.SingleAsync()).Quantity.Should().Be(10);
        var import = await context.OpeningStockImports.Include(value => value.Lines).SingleAsync();
        import.ApprovalReference.Should().Be("approval-1");
        import.Lines.Single().UnitCost.Should().Be(0);

        var second = await service.ReplayAsync(request);

        second.AlreadyApplied.Should().BeTrue();
        second.AppliedRows.Should().Be(0);
        (await context.StockInHand.SingleAsync()).Quantity.Should().Be(10);
        (await context.OpeningStockImports.CountAsync()).Should().Be(1);

        await FluentActions.Invoking(() => service.ReplayAsync(request with
        {
            Csv = request.Csv.Replace(",10,0", ",11,0", StringComparison.Ordinal)
        })).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ReplayAsync_CreatesOpeningMovementAndValuationAtCutover()
    {
        await using var context = CreateContext();
        var item = new Item { ExternalId = "item-1", ItemCode = "SKU-1", Description = "Widget", IsActive = true };
        var location = new Location { Id = 7, Name = "Main" };
        context.Items.Add(item);
        context.Locations.Add(location);
        await context.SaveChangesAsync();
        var cutoverAt = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

        await CreateService(context).ReplayAsync(new(
            "external_reference,item_external_id,location_id,quantity,unit_cost\nopen-1,item-1,7,10,12.5",
            "import-1",
            "approval-1",
            cutoverAt));

        var import = await context.OpeningStockImports.SingleAsync();
        import.CutoverAt.Should().Be(cutoverAt);
        (await context.StockTransactions.SingleAsync()).TransactionType.Should().Be(TransactionType.Opening);
        (await context.StockTransactions.SingleAsync()).TransactionDate.Should().Be(cutoverAt);
        var bucket = await context.StockValuationBuckets.SingleAsync();
        bucket.Quantity.Should().Be(10);
        bucket.Value.Should().Be(125m);
        var entry = await context.StockValuationEntries.SingleAsync();
        entry.StockTransactionId.Should().Be((await context.StockTransactions.SingleAsync()).Id);
        entry.TotalValue.Should().Be(125m);
        (await context.OpeningStockImportLines.SingleAsync()).StockTransactionId.Should().Be(entry.StockTransactionId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplayAsync_RejectsCutoverBeforeExistingTransferMovementWithoutMutation(bool movementArrivesAtLocation)
    {
        await using var context = CreateContext();
        var item = new Item { ExternalId = "item-1", ItemCode = "SKU-1", Description = "Widget", IsActive = true };
        var source = new Location { Id = 7, Name = "Source" };
        var destination = new Location { Id = 8, Name = "Destination" };
        context.Items.Add(item);
        context.Locations.AddRange(source, destination);
        context.StockInHand.Add(new StockInHand { Item = item, Location = destination, Quantity = 10 });
        await context.SaveChangesAsync();

        var cutoverAt = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        context.StockTransactions.Add(new StockTransaction
        {
            ItemId = item.Id,
            FromLocationId = movementArrivesAtLocation ? source.Id : destination.Id,
            ToLocationId = movementArrivesAtLocation ? destination.Id : source.Id,
            Quantity = 10,
            TransactionType = TransactionType.Transfer,
            TransactionDate = cutoverAt.AddSeconds(1)
        });
        await context.SaveChangesAsync();

        await FluentActions.Invoking(() => CreateService(context).ReplayAsync(new(
            "external_reference,item_external_id,location_id,quantity,unit_cost\nopen-1,item-1,8,10,12.5",
            "import-1",
            "approval-1",
            cutoverAt)))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot precede or coincide with an existing stock movement*");

        (await context.StockInHand.SingleAsync()).Quantity.Should().Be(10);
        (await context.StockTransactions.CountAsync()).Should().Be(1);
        (await context.OpeningStockImports.CountAsync()).Should().Be(0);
        (await context.StockValuationBuckets.CountAsync()).Should().Be(0);
        (await context.StockValuationEntries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ReplayAsync_AllowsLaterQuarantineBecauseItDoesNotChangeOnHandQuantity()
    {
        await using var context = CreateContext();
        var item = new Item { ExternalId = "item-1", ItemCode = "SKU-1", Description = "Widget", IsActive = true };
        var location = new Location { Id = 7, Name = "Main" };
        context.Items.Add(item);
        context.Locations.Add(location);
        context.StockInHand.Add(new StockInHand
        {
            Item = item,
            Location = location,
            Quantity = 10,
            QuarantinedQuantity = 2
        });
        await context.SaveChangesAsync();

        var cutoverAt = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        context.StockTransactions.Add(new StockTransaction
        {
            ItemId = item.Id,
            FromLocationId = location.Id,
            Quantity = 2,
            TransactionType = TransactionType.Quarantine,
            TransactionDate = cutoverAt.AddSeconds(1)
        });
        await context.SaveChangesAsync();

        var result = await CreateService(context).ReplayAsync(new(
            "external_reference,item_external_id,location_id,quantity,unit_cost\nopen-1,item-1,7,10,12.5",
            "import-1",
            "approval-1",
            cutoverAt));

        result.AppliedRows.Should().Be(1);
        (await context.StockInHand.SingleAsync()).Quantity.Should().Be(10);
        (await context.StockTransactions.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task ReplayAsync_RejectsUnreconciledExistingQuantityWithoutMutation()
    {
        await using var context = CreateContext();
        var item = new Item { ExternalId = "item-1", ItemCode = "SKU-1", Description = "Widget", IsActive = true };
        var location = new Location { Id = 7, Name = "Main" };
        context.Items.Add(item);
        context.Locations.Add(location);
        context.StockInHand.Add(new StockInHand { Item = item, Location = location, Quantity = 5 });
        await context.SaveChangesAsync();

        await FluentActions.Invoking(() => CreateService(context).ReplayAsync(new(
            "external_reference,item_external_id,location_id,quantity,unit_cost\nopen-1,item-1,7,10,12.5",
            "import-1",
            "approval-1"))).Should().ThrowAsync<InvalidOperationException>();

        (await context.StockInHand.SingleAsync()).Quantity.Should().Be(5);
        (await context.OpeningStockImports.CountAsync()).Should().Be(0);
        (await context.StockValuationBuckets.CountAsync()).Should().Be(0);
        (await context.StockTransactions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ReverseAsync_UsesForwardStockEffectsAndIsIdempotent()
    {
        await using var context = CreateContext();
        context.Items.Add(new Item { ExternalId = "item-1", ItemCode = "SKU-1", Description = "Widget", IsActive = true });
        context.Locations.Add(new Location { Id = 7, Name = "Main" });
        await context.SaveChangesAsync();
        var service = CreateService(context);
        await service.ReplayAsync(new(
            "external_reference,item_external_id,location_id,quantity,unit_cost\nopen-1,item-1,7,10,12.5",
            "import-1",
            "approval-1"));
        var stockService = new Mock<IStockService>();
        stockService.Setup(value => value.SellStockAsync(1, 7, 10, It.IsAny<string>(), null, null, null))
            .Returns(Task.CompletedTask);
        var reversalService = new OpeningStockImportService(
            context,
            new TestTenantContext("test-tenant"),
            new UnitOfWork(context),
            new HttpContextAccessor(),
            stockService.Object);

        var request = new OpeningStockReversalRequest("import-1", "correction-1", "approval-2", "Wrong opening count");
        (await reversalService.ReverseAsync(request)).AlreadyApplied.Should().BeFalse();
        (await reversalService.ReverseAsync(request)).AlreadyApplied.Should().BeTrue();
        (await context.OpeningStockCorrections.CountAsync()).Should().Be(1);
        stockService.Verify(value => value.SellStockAsync(1, 7, 10, It.IsAny<string>(), null, null, null), Times.Once);
    }

    [Fact]
    public async Task PreviewAsync_ReportsCurrentVersusApprovedQuantities()
    {
        await using var context = CreateContext();
        var item = new Item { ExternalId = "item-1", ItemCode = "SKU-1", Description = "Widget", IsActive = true };
        var location = new Location { Id = 7, Name = "Main" };
        context.Items.Add(item);
        context.Locations.Add(location);
        context.StockInHand.Add(new StockInHand { Item = item, Location = location, Quantity = 5 });
        await context.SaveChangesAsync();

        var result = await CreateService(context).PreviewAsync(new(
            "external_reference,item_external_id,location_id,quantity,unit_cost\nopen-1,item-1,7,10,12.5"));

        result.Discrepancies.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new OpeningStockDiscrepancy(1, 7, 5, 10, 5));
    }

    [Fact]
    public async Task ApprovedOpeningImport_IsImmutableInEf()
    {
        await using var context = CreateContext();
        context.Items.Add(new Item { ExternalId = "item-1", ItemCode = "SKU-1", Description = "Widget", IsActive = true });
        context.Locations.Add(new Location { Id = 7, Name = "Main" });
        await context.SaveChangesAsync();
        await CreateService(context).ReplayAsync(new(
            "external_reference,item_external_id,location_id,quantity,unit_cost\nopen-1,item-1,7,10,12.5",
            "import-1",
            "approval-1"));
        var import = await context.OpeningStockImports.SingleAsync();
        import.ApprovalReference = "changed";

        await FluentActions.Invoking(() => context.SaveChangesAsync())
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ReplayAsync_RejectsInvalidRowsWithoutMutation()
    {
        await using var context = CreateContext();
        context.Items.Add(new Item
        {
            ExternalId = "item-1",
            ItemCode = "SKU-1",
            Description = "Widget",
            IsActive = true
        });
        context.Locations.Add(new Location { Id = 7, Name = "Main" });
        await context.SaveChangesAsync();

        var result = await CreateService(context).ReplayAsync(new(
            "external_reference,item_external_id,location_id,quantity,unit_cost\nopen-1,item-1,7,10,",
            "import-invalid",
            "approval-invalid"));

        result.Valid.Should().Be(0);
        result.Rejected.Should().Be(1);
        (await context.StockInHand.CountAsync()).Should().Be(0);
        (await context.OpeningStockImports.CountAsync()).Should().Be(0);
    }

    private static OpeningStockImportService CreateService(InventoryDbContext context) =>
        new(context, new TestTenantContext("test-tenant"), new UnitOfWork(context), new HttpContextAccessor());

    private static InventoryDbContext CreateContext(string? databaseName = null, string tenantId = "test-tenant") => new(
        new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString())
            .Options,
        new TestTenantContext(tenantId));
}
