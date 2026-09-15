using FluentAssertions;
using InventoryManagementSystem.Core.Entities;
using InventoryManagementSystem.Infrastructure.Data;
using InventoryManagementSystem.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace InventoryManagementSystem.Tests.Infrastructure;

public class RepositoryTests
{
    [Fact]
    public async Task Query_allows_composition_before_materialization()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var context = new InventoryDbContext(options);
        context.Items.Add(new Item
        {
            ItemCode = "COMPOSE-001",
            Description = "Composable query",
            Rate = 10m
        });
        await context.SaveChangesAsync();

        var repository = new Repository<Item>(context);
        var projectedQuery = repository.Query()
            .Where(item => item.ItemCode.StartsWith("COMPOSE"))
            .Select(item => item.ItemCode);

        projectedQuery.Should().BeAssignableTo<IQueryable<string>>();
        (await projectedQuery.SingleAsync()).Should().Be("COMPOSE-001");
    }
}
