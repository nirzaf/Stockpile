using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Tests.Infrastructure;

public class TenantIsolationTests
{
    [Fact]
    public async Task Queries_only_return_rows_for_the_current_tenant()
    {
        var databaseName = Guid.NewGuid().ToString();
        await using (var tenantAContext = CreateContext(databaseName, "tenant-a"))
        {
            tenantAContext.Items.Add(new Item
            {
                ItemCode = "TENANT-A-001",
                Description = "Tenant A item",
                Rate = 10m,
                TenantId = "tenant-b"
            });
            await tenantAContext.SaveChangesAsync();
        }

        await using (var tenantBContext = CreateContext(databaseName, "tenant-b"))
        {
            tenantBContext.Items.Add(new Item
            {
                ItemCode = "TENANT-B-001",
                Description = "Tenant B item",
                Rate = 20m
            });
            await tenantBContext.SaveChangesAsync();
        }

        await using var readTenantAContext = CreateContext(databaseName, "tenant-a");
        var tenantAItems = await readTenantAContext.Items.ToListAsync();

        tenantAItems.Should().ContainSingle();
        tenantAItems[0].TenantId.Should().Be("tenant-a");
        tenantAItems[0].ItemCode.Should().Be("TENANT-A-001");
    }

    private static InventoryDbContext CreateContext(string databaseName, string tenantId)
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
        return new InventoryDbContext(options, new TestTenantContext(tenantId));
    }
}
