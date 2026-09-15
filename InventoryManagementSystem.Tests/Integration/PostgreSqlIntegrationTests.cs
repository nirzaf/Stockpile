using FluentAssertions;
using InventoryManagementSystem.Core.Entities;
using InventoryManagementSystem.Infrastructure.Data;
using InventoryManagementSystem.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace InventoryManagementSystem.Tests.Integration;

/// <summary>
/// Relational behavior that cannot be proven by the fast InMemory provider.
/// The collection is deliberately separate and is enabled in CI by RUN_POSTGRES_TESTS.
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class PostgreSqlIntegrationTests
{
    private readonly PostgreSqlIntegrationFixture _fixture;

    public PostgreSqlIntegrationTests(PostgreSqlIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [PostgreSqlFact]
    public async Task Migrations_create_relational_schema_and_tenant_scoped_unique_constraints()
    {
        _fixture.EnsureEnabled();
        var itemCode = Unique("MIGRATION");

        await using (var tenantA = _fixture.CreateContext("postgres-tenant-a"))
        {
            tenantA.Items.Add(new Item { ItemCode = itemCode, Description = "Tenant A", Rate = 10m });
            await tenantA.SaveChangesAsync();
        }

        await using (var duplicateTenantA = _fixture.CreateContext("postgres-tenant-a"))
        {
            duplicateTenantA.Items.Add(new Item { ItemCode = itemCode, Description = "Duplicate", Rate = 20m });
            var act = () => duplicateTenantA.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>();
        }

        await using var tenantB = _fixture.CreateContext("postgres-tenant-b");
        tenantB.Items.Add(new Item { ItemCode = itemCode, Description = "Tenant B", Rate = 30m });
        await tenantB.SaveChangesAsync();
        (await tenantB.Items.CountAsync()).Should().Be(1);
    }

    [PostgreSqlFact]
    public async Task Global_filters_isolate_tenant_rows_in_PostgreSQL()
    {
        _fixture.EnsureEnabled();
        var tenantAItem = Unique("TENANT-A");
        var tenantBItem = Unique("TENANT-B");

        await using (var context = _fixture.CreateContext("filter-tenant-a"))
        {
            context.Items.Add(new Item { ItemCode = tenantAItem, Description = "A", Rate = 1m });
            await context.SaveChangesAsync();
        }

        await using (var context = _fixture.CreateContext("filter-tenant-b"))
        {
            context.Items.Add(new Item { ItemCode = tenantBItem, Description = "B", Rate = 1m });
            await context.SaveChangesAsync();
        }

        await using var tenantARead = _fixture.CreateContext("filter-tenant-a");
        var visibleItems = await tenantARead.Items.ToListAsync();

        visibleItems.Should().ContainSingle();
        visibleItems[0].ItemCode.Should().Be(tenantAItem);
    }

    [PostgreSqlFact]
    public async Task PostgreSQL_search_translates_case_insensitive_item_search()
    {
        _fixture.EnsureEnabled();
        await using var context = _fixture.CreateContext("search-tenant");
        context.Items.AddRange(
            new Item { ItemCode = Unique("SEARCH-WIDGET"), Description = "Warehouse Widget", Rate = 10m },
            new Item { ItemCode = Unique("SEARCH-OTHER"), Description = "Different product", Rate = 20m });
        await context.SaveChangesAsync();

        var repository = new ItemRepository(context);
        var result = await repository.SearchAsync("warehouse widget");

        result.Should().ContainSingle(item => item.Description == "Warehouse Widget");
    }

    [PostgreSqlFact]
    public async Task SaveChanges_writes_audit_rows_to_PostgreSQL()
    {
        _fixture.EnsureEnabled();
        await using var context = _fixture.CreateContext("audit-tenant");
        var item = new Item { ItemCode = Unique("AUDIT"), Description = "Audited item", Rate = 25m };

        context.Items.Add(item);
        await context.SaveChangesAsync();

        var audit = await context.AuditLogs
            .Where(log => log.EntityName == nameof(Item) && log.Action == "Insert")
            .OrderByDescending(log => log.Id)
            .FirstOrDefaultAsync();

        audit.Should().NotBeNull();
        audit!.TenantId.Should().Be("audit-tenant");
        audit.KeyValues.Should().Contain(item.Id.ToString());
    }

    [PostgreSqlFact]
    public async Task Identity_users_are_persisted_in_the_relational_schema()
    {
        _fixture.EnsureEnabled();
        await using var context = _fixture.CreateContext("identity-tenant");
        var user = new ApplicationUser
        {
            UserName = Unique("identity-user"),
            NormalizedUserName = Unique("IDENTITY-USER"),
            Email = "identity@example.test",
            NormalizedEmail = "IDENTITY@EXAMPLE.TEST",
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString()
        };

        context.Users.Add(user);
        await context.SaveChangesAsync();

        (await context.Users.SingleAsync(saved => saved.Id == user.Id)).TenantId
            .Should().Be("identity-tenant");
    }

    [PostgreSqlFact]
    public async Task Explicit_transactions_rollback_rows_in_PostgreSQL()
    {
        _fixture.EnsureEnabled();
        var itemCode = Unique("ROLLBACK");

        await using (var context = _fixture.CreateContext("transaction-tenant"))
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            context.Items.Add(new Item { ItemCode = itemCode, Description = "Rolled back", Rate = 1m });
            await context.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        await using var verify = _fixture.CreateContext("transaction-tenant");
        (await verify.Items.CountAsync(item => item.ItemCode == itemCode)).Should().Be(0);
    }

    [PostgreSqlFact]
    public async Task PostgreSQL_xmin_detects_concurrent_stock_updates()
    {
        _fixture.EnsureEnabled();
        var tenantId = Unique("concurrency-tenant");
        int stockId;

        await using (var setup = _fixture.CreateContext(tenantId))
        {
            var item = new Item { ItemCode = Unique("CONCURRENCY"), Description = "Concurrent", Rate = 1m };
            var location = new Location { Name = Unique("location") };
            setup.Items.Add(item);
            setup.Locations.Add(location);
            await setup.SaveChangesAsync();

            var stock = new StockInHand
            {
                ItemId = item.Id,
                LocationId = location.Id,
                Quantity = 10
            };
            setup.StockInHand.Add(stock);
            await setup.SaveChangesAsync();
            stockId = stock.Id;
        }

        await using var first = _fixture.CreateContext(tenantId);
        await using var second = _fixture.CreateContext(tenantId);
        var firstStock = await first.StockInHand.SingleAsync(stock => stock.Id == stockId);
        var secondStock = await second.StockInHand.SingleAsync(stock => stock.Id == stockId);

        firstStock.Quantity = 11;
        secondStock.Quantity = 12;
        await first.SaveChangesAsync();

        var act = () => second.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
