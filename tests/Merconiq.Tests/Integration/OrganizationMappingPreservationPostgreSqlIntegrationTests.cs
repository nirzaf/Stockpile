using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Infrastructure.Data;
using Merconiq.Infrastructure.Repositories;
using Merconiq.Infrastructure.Services;
using Merconiq.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class OrganizationMappingPreservationPostgreSqlIntegrationTests(
    PostgreSqlIntegrationFixture fixture)
{
    [PostgreSqlFact]
    public async Task Explicit_synthetic_location_mappings_preserve_stock_and_reject_cross_tenant_branch()
    {
        fixture.EnsureEnabled();
        var suffix = Guid.NewGuid().ToString("N");
        var tenantA = $"mapping-a-{suffix}";
        var tenantB = $"mapping-b-{suffix}";

        // These unassigned locations and their stock rows represent pre-existing data.
        // The mapping list is deliberately explicit and synthetic; it is not an owner mapping.
        var syntheticMappings = new[]
        {
            await SeedExistingStockAsync(fixture, tenantA, $"A-{suffix[..10]}", 37, 4),
            await SeedExistingStockAsync(fixture, tenantB, $"B-{suffix[..10]}", 19, 2)
        };

        foreach (var mapping in syntheticMappings)
        {
            await using var context = fixture.CreateContext(mapping.TenantId);
            var service = CreateOrganizationService(context, mapping.TenantId);

            var assignForeignBranch = () => service.AssignLocationBranchAsync(
                mapping.LocationId,
                syntheticMappings.Single(candidate => candidate.TenantId != mapping.TenantId).BranchId);

            await assignForeignBranch.Should().ThrowAsync<KeyNotFoundException>();
            (await context.Locations.SingleAsync(location => location.Id == mapping.LocationId))
                .BranchId.Should().BeNull();
        }

        foreach (var mapping in syntheticMappings)
        {
            await using var context = fixture.CreateContext(mapping.TenantId);
            await CreateOrganizationService(context, mapping.TenantId)
                .AssignLocationBranchAsync(mapping.LocationId, mapping.BranchId);
        }

        foreach (var mapping in syntheticMappings)
        {
            await using var context = fixture.CreateContext(mapping.TenantId);
            var location = await context.Locations.SingleAsync(row => row.Id == mapping.LocationId);
            var stock = await context.StockInHand.SingleAsync(row => row.Id == mapping.StockId);
            var branch = await context.Branches.SingleAsync(row => row.Id == mapping.BranchId);

            location.BranchId.Should().Be(mapping.BranchId);
            location.Id.Should().Be(mapping.LocationId);
            location.TenantId.Should().Be(mapping.TenantId);
            branch.CompanyId.Should().Be(mapping.CompanyId);
            branch.TenantId.Should().Be(mapping.TenantId);

            stock.Id.Should().Be(mapping.StockId);
            stock.ItemId.Should().Be(mapping.ItemId);
            stock.LocationId.Should().Be(mapping.LocationId);
            stock.TenantId.Should().Be(mapping.TenantId);
            stock.Quantity.Should().Be(mapping.Quantity);
            stock.ReservedQuantity.Should().Be(mapping.ReservedQuantity);
        }
    }

    private static async Task<SyntheticLocationMapping> SeedExistingStockAsync(
        PostgreSqlIntegrationFixture fixture,
        string tenantId,
        string code,
        int quantity,
        int reservedQuantity)
    {
        await using var context = fixture.CreateContext(tenantId);
        var company = new Company
        {
            Code = $"CO-{code}",
            LegalName = $"Synthetic company {code}",
            BaseCurrency = "USD"
        };
        var branch = new Branch
        {
            Company = company,
            Code = $"BR-{code}",
            Name = $"Synthetic branch {code}"
        };
        var location = new Location { Name = $"Existing unassigned location {code}" };
        var item = new Item
        {
            ItemCode = $"ITEM-{code}",
            Description = $"Synthetic item {code}",
            Rate = 3.25m
        };
        var stock = new StockInHand
        {
            Item = item,
            Location = location,
            Quantity = quantity,
            ReservedQuantity = reservedQuantity
        };

        context.AddRange(company, branch, location, item, stock);
        await context.SaveChangesAsync();

        return new SyntheticLocationMapping(
            tenantId,
            company.Id,
            branch.Id,
            location.Id,
            item.Id,
            stock.Id,
            quantity,
            reservedQuantity);
    }

    private static OrganizationService CreateOrganizationService(
        InventoryDbContext context,
        string tenantId) => new(
        new Repository<Company>(context),
        new Repository<Branch>(context),
        new Repository<Location>(context),
        new UnitOfWork(context),
        new TestTenantContext(tenantId),
        context);

    private sealed record SyntheticLocationMapping(
        string TenantId,
        int CompanyId,
        int BranchId,
        int LocationId,
        int ItemId,
        int StockId,
        int Quantity,
        int ReservedQuantity);
}
