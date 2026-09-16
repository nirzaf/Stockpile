using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Infrastructure.Data;
using Merconiq.Infrastructure.Services;
using Merconiq.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

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

    private static OpeningStockImportService CreateService(InventoryDbContext context) =>
        new(context, new TestTenantContext("test-tenant"));

    private static InventoryDbContext CreateContext(string? databaseName = null, string tenantId = "test-tenant") => new(
        new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString())
            .Options,
        new TestTenantContext(tenantId));
}
