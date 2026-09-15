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

        await using var context = new InventoryDbContext(options, new TestTenantContext("default"));
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

    [Fact]
    public async Task ItemRepository_Search_is_case_insensitive_across_catalog_fields()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var context = new InventoryDbContext(options, new TestTenantContext("default"));
        context.Items.AddRange(
            new Item { ItemCode = "SEARCH-001", Description = "Widget", Barcode = "12345", Rate = 10m },
            new Item { ItemCode = "OTHER-001", Description = "Different", Rate = 20m });
        await context.SaveChangesAsync();

        var repository = new ItemRepository(context);

        var results = await repository.SearchAsync("widget");

        results.Should().ContainSingle(item => item.ItemCode == "SEARCH-001");
    }

    [Fact]
    public void Item_barcode_model_index_is_unique()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var context = new InventoryDbContext(options, new TestTenantContext("default"));
        var barcodeIndex = context.Model.FindEntityType(typeof(Item))!
            .GetIndexes()
            .Single(index => index.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { nameof(Item.TenantId), nameof(Item.Barcode) }));

        barcodeIndex.IsUnique.Should().BeTrue();
    }

    [Fact]
    public async Task GetPagedAsync_returns_stable_membership_across_repeated_calls()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var context = new InventoryDbContext(options, new TestTenantContext("default"));
        context.Items.AddRange(
            new Item { ItemCode = "PAGE-003", Description = "Third", Rate = 3m },
            new Item { ItemCode = "PAGE-001", Description = "First", Rate = 1m },
            new Item { ItemCode = "PAGE-002", Description = "Second", Rate = 2m });
        await context.SaveChangesAsync();

        var repository = new Repository<Item>(context);
        var first = (await repository.GetPagedAsync(2, 2)).Select(item => item.Id).ToArray();
        var second = (await repository.GetPagedAsync(2, 2)).Select(item => item.Id).ToArray();

        second.Should().Equal(first);
    }
}
