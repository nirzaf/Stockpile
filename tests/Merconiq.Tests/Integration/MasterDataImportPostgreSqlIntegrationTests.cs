using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Merconiq.Infrastructure.Repositories;
using Merconiq.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class MasterDataImportPostgreSqlIntegrationTests(PostgreSqlIntegrationFixture fixture)
{
    [PostgreSqlFact]
    public async Task Controlled_onboarding_is_idempotent_and_atomic_across_owned_masters()
    {
        fixture.EnsureEnabled();
        var tenantId = $"onboarding-{Guid.NewGuid():N}";
        var serviceContext = fixture.CreateContext(tenantId);
        await using (serviceContext)
        {
            var companyCsv = "external_id,code,legal_name,trading_name,registration_number,tax_identifier,base_currency,country_code,currency_scale,is_active\n"
                + "company-1,C-1,Acme Trading,Acme,,TAX-1,QAR,QA,2,true";
            var companyResult = await CreateService(serviceContext).ImportCompaniesAsync(
                new ImportCompaniesRequest(companyCsv, false));
            companyResult.Created.Should().Be(1);
            var companyId = await serviceContext.Companies.Select(company => company.Id).SingleAsync();

            var branchCsv = "external_id,code,name,address,time_zone_id,is_active\n"
                + "branch-1,BR-1,Main,,Asia/Qatar,true";
            (await CreateService(serviceContext).ImportBranchesAsync(
                new ImportBranchesRequest(branchCsv, false, companyId))).Created.Should().Be(1);

            var locationCsv = "external_id,branch_external_id,name,address\n"
                + "location-1,branch-1,Warehouse A,\n"
                + "location-2,missing,Warehouse B,";
            var rejected = await CreateService(serviceContext).ImportLocationsAsync(
                new ImportLocationsRequest(locationCsv, false, companyId));
            rejected.Rejected.Should().Be(1);
            (await serviceContext.Locations.CountAsync()).Should().Be(0);

            var validLocationCsv = "external_id,branch_external_id,name,address\nlocation-1,branch-1,Warehouse A,";
            (await CreateService(serviceContext).ImportLocationsAsync(
                new ImportLocationsRequest(validLocationCsv, false, companyId))).Created.Should().Be(1);
            (await CreateService(serviceContext).ImportLocationsAsync(
                new ImportLocationsRequest(validLocationCsv, false, companyId))).Unchanged.Should().Be(1);

            var supplierCsv = "external_id,name,contact_person,phone,email,address\n"
                + "supplier-1,Acme Supplies,Buyer,+97400000000,buyer@example.test,Doha";
            (await CreateService(serviceContext).ImportSuppliersAsync(
                new ImportSuppliersRequest(supplierCsv, false, companyId))).Created.Should().Be(1);

            serviceContext.UnitsOfMeasure.Add(new UnitOfMeasure
            {
                ExternalId = "kg", Code = "KG", Name = "Kilogram"
            });
            await serviceContext.SaveChangesAsync();
        }

        await using (var otherTenantContext = fixture.CreateContext($"other-{Guid.NewGuid():N}"))
        {
            var foreignCompanyId = 0;
            await using (var source = fixture.CreateContext(tenantId))
                foreignCompanyId = await source.Companies.Select(company => company.Id).SingleAsync();

            await FluentActions.Invoking(() => CreateService(otherTenantContext).ImportSuppliersAsync(
                    new ImportSuppliersRequest(
                        "external_id,name,contact_person,phone,email,address\nsupplier-2,Other,,,,",
                        false, foreignCompanyId)))
                .Should().ThrowAsync<ArgumentException>();
        }

        await using var replayContext = fixture.CreateContext(tenantId);
        var itemCsv = "external_id,item_code,description,rate,base_unit_external_id,purchase_unit_external_id,sales_unit_external_id,purchase_to_base_factor,sales_to_base_factor,quantity_precision,whole_unit_only,supplier_external_id\n"
            + "item-1,SKU-1,Widget,12.50,kg,,,1,1,2,false,supplier-1";
        var company = await replayContext.Companies.SingleAsync();
        var itemResult = await CreateService(replayContext).ImportItemsAsync(
            new ImportItemsRequest(itemCsv, false, company.Id));
        itemResult.Created.Should().Be(1);
        (await replayContext.AuditLogs.CountAsync(log => log.EntityName == nameof(Item))).Should().Be(1);
    }

    [PostgreSqlFact]
    public async Task Item_import_is_idempotent_and_persists_unit_ownership()
    {
        fixture.EnsureEnabled();
        var tenantId = $"item-import-{Guid.NewGuid():N}";
        var csv = "external_id,item_code,description,rate,base_unit_external_id,purchase_unit_external_id,sales_unit_external_id,purchase_to_base_factor,sales_to_base_factor,quantity_precision,whole_unit_only\n"
            + "item-1,SKU-1,Widget,12.50,kg,,,1,1,2,false";

        await using (var setup = fixture.CreateContext(tenantId))
        {
            setup.UnitsOfMeasure.Add(new UnitOfMeasure { ExternalId = "kg", Code = "KG", Name = "Kilogram" });
            await setup.SaveChangesAsync();
        }

        await using (var firstContext = fixture.CreateContext(tenantId))
        {
            var result = await CreateService(firstContext).ImportItemsAsync(new ImportItemsRequest(csv, false));
            result.Created.Should().Be(1);
        }

        await using (var replayContext = fixture.CreateContext(tenantId))
        {
            var result = await CreateService(replayContext).ImportItemsAsync(new ImportItemsRequest(csv, false));
            result.Unchanged.Should().Be(1);
            result.Rejected.Should().Be(0);
            var item = await replayContext.Items.SingleAsync();
            item.ExternalId.Should().Be("item-1");
            item.BaseUnitId.Should().Be(await replayContext.UnitsOfMeasure.Select(unit => unit.Id).SingleAsync());
        }
    }

    private static MasterDataImportService CreateService(InventoryDbContext context) => new(
        new Repository<Merconiq.Core.Entities.UnitOfMeasure>(context),
        new Repository<Item>(context),
        new Repository<Company>(context),
        new Repository<Branch>(context),
        new Repository<Location>(context),
        new Repository<Supplier>(context),
        context,
        new UnitOfWork(context));
}
