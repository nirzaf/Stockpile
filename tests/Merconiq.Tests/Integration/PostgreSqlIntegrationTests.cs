using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Services;
using Merconiq.Infrastructure.Data;
using Merconiq.Infrastructure.Repositories;
using Merconiq.Infrastructure.Services;
using Merconiq.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Merconiq.Tests.Integration;

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
    public async Task Stock_locations_enforce_tenant_and_company_ownership()
    {
        _fixture.EnsureEnabled();
        var tenantA = Unique("stock-company-a");
        var tenantB = Unique("stock-company-b");
        int itemId;
        int companyALocationId;
        int companyBLocationId;
        int otherTenantLocationId;

        await using (var setup = _fixture.CreateContext(tenantA))
        {
            var companyA = new Company { Code = $"A-{Guid.NewGuid():N}"[..12], LegalName = "Company A" };
            var companyB = new Company { Code = $"B-{Guid.NewGuid():N}"[..12], LegalName = "Company B" };
            setup.Companies.AddRange(companyA, companyB);
            await setup.SaveChangesAsync();

            var branchA = new Branch { CompanyId = companyA.Id, Code = $"A-{Guid.NewGuid():N}"[..12], Name = "Branch A" };
            var branchB = new Branch { CompanyId = companyB.Id, Code = $"B-{Guid.NewGuid():N}"[..12], Name = "Branch B" };
            setup.Branches.AddRange(branchA, branchB);
            await setup.SaveChangesAsync();

            var item = new Item { ItemCode = Unique("STOCK-ITEM"), Description = "Ownership fixture", Rate = 1m };
            var locationA = new Location { Name = Unique("location-a"), BranchId = branchA.Id };
            var locationB = new Location { Name = Unique("location-b"), BranchId = branchB.Id };
            setup.Items.Add(item);
            setup.Locations.AddRange(locationA, locationB);
            await setup.SaveChangesAsync();

            setup.StockInHand.Add(new StockInHand { ItemId = item.Id, LocationId = locationA.Id, Quantity = 10 });
            await setup.SaveChangesAsync();
            itemId = item.Id;
            companyALocationId = locationA.Id;
            companyBLocationId = locationB.Id;
        }

        await using (var setup = _fixture.CreateContext(tenantB))
        {
            var location = new Location { Name = Unique("other-tenant-location") };
            setup.Locations.Add(location);
            await setup.SaveChangesAsync();
            otherTenantLocationId = location.Id;
        }

        await using (var operation = _fixture.CreateContext(tenantA))
        {
            var dispatcher = new Mock<IWebhookDispatcher>();
            var stockService = new StockService(
                new Repository<StockInHand>(operation),
                new Repository<StockTransaction>(operation),
                new Repository<Item>(operation),
                new Repository<Location>(operation),
                new Repository<Branch>(operation),
                new UnitOfWork(operation),
                dispatcher.Object,
                new TestTenantContext(tenantA),
                NullLogger<StockService>.Instance);

            var crossCompany = () => stockService.TransferStockAsync(
                itemId, companyALocationId, companyBLocationId, 1, "cross-company");
            await crossCompany.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Cross-company stock transfers are not supported.");

            var crossTenant = () => stockService.ReceiveStockAsync(itemId, otherTenantLocationId, 1, "cross-tenant");
            await crossTenant.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Location does not exist in the current tenant or is deleted.");
        }

        await using (var forged = _fixture.CreateContext(tenantA))
        {
            forged.StockInHand.Add(new StockInHand
            {
                ItemId = itemId,
                LocationId = otherTenantLocationId,
                Quantity = 1
            });

            var act = () => forged.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>();
        }
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
    public async Task Low_stock_delivery_is_persisted_after_the_operation_scope_is_disposed()
    {
        _fixture.EnsureEnabled();
        var tenantId = Unique("low-stock-tenant");
        var tenant = new TestTenantContext(tenantId);
        int itemId;
        int locationId;

        await using (var setup = _fixture.CreateContext(tenantId))
        {
            var item = new Item
            {
                ItemCode = Unique("LOW-STOCK"),
                Description = "Low-stock integration fixture",
                Rate = 10m,
                ReorderLevel = 10
            };
            var location = new Location { Name = Unique("low-stock-location") };
            setup.Items.Add(item);
            setup.Locations.Add(location);
            await setup.SaveChangesAsync();

            setup.StockInHand.Add(new StockInHand
            {
                ItemId = item.Id,
                LocationId = location.Id,
                Quantity = 15
            });
            setup.WebhookSubscriptions.Add(new WebhookSubscription
            {
                TenantId = tenantId,
                EventType = "Stock.Low",
                Url = "https://hooks.example.test/low-stock"
            });
            await setup.SaveChangesAsync();
            itemId = item.Id;
            locationId = location.Id;
        }

        using var services = new ServiceCollection().BuildServiceProvider();
        var httpClientFactory = new Mock<IHttpClientFactory>();
        await using (var operation = _fixture.CreateContext(tenantId))
        {
            var dispatcher = new WebhookDispatcher(
                services,
                httpClientFactory.Object,
                NullLogger<WebhookDispatcher>.Instance,
                operation);
            var stockService = new StockService(
                new Repository<StockInHand>(operation),
                new Repository<StockTransaction>(operation),
                new Repository<Item>(operation),
                new Repository<Location>(operation),
                new Repository<Branch>(operation),
                new UnitOfWork(operation),
                dispatcher,
                tenant,
                NullLogger<StockService>.Instance);

            await stockService.SellStockAsync(itemId, locationId, 5, "reorder threshold");
        }

        await using var verify = _fixture.CreateContext(tenantId);
        var delivery = await verify.WebhookDeliveries
            .SingleAsync(item => item.EventType == "Stock.Low");
        delivery.TenantId.Should().Be(tenantId);
        delivery.Status.Should().Be(WebhookDeliveryStatus.Pending);
        delivery.Payload.Should().Contain("\"TotalStock\":10");
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
