using FluentAssertions;
using InventoryManagementSystem.Core.Entities;
using InventoryManagementSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace InventoryManagementSystem.Tests.Infrastructure;

public class SoftDeleteTests
{
    [Fact]
    public async Task Deleting_a_soft_deletable_entity_preserves_the_row_but_hides_it_by_default()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var context = new InventoryDbContext(options);
        var item = new Item
        {
            ItemCode = "SOFT-DELETE-001",
            Description = "Retained for audit",
            Rate = 10m
        };
        context.Items.Add(item);
        await context.SaveChangesAsync();

        context.Items.Remove(item);
        await context.SaveChangesAsync();

        (await context.Items.SingleOrDefaultAsync(i => i.Id == item.Id)).Should().BeNull();
        var retained = await context.Items.IgnoreQueryFilters().SingleAsync(i => i.Id == item.Id);
        retained.IsDeleted.Should().BeTrue();
    }
}
