using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Merconiq.Infrastructure.Repositories;
using Merconiq.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Tests.Infrastructure;

public sealed class MasterDataImportServiceTests
{
    [Fact]
    public async Task ImportCompanies_is_idempotent_and_preserves_scope()
    {
        await using var context = CreateContext(Guid.NewGuid().ToString(), "tenant-a");
        var service = CreateService(context);
        var csv = "external_id,code,legal_name,trading_name,registration_number,tax_identifier,base_currency,country_code,currency_scale,is_active\n"
            + "company-1,C-1,Acme Trading,Acme,,TAX-1,QAR,QA,2,true";

        var created = await service.ImportCompaniesAsync(new ImportCompaniesRequest(csv, false));
        var replayed = await service.ImportCompaniesAsync(new ImportCompaniesRequest(csv, false));

        created.Created.Should().Be(1);
        replayed.Unchanged.Should().Be(1);
        context.Companies.Count().Should().Be(1);
        (await context.Companies.SingleAsync()).ExternalId.Should().Be("company-1");
    }

    [Fact]
    public async Task ImportLocations_rejects_one_row_without_mutating_valid_rows()
    {
        await using var context = CreateContext(Guid.NewGuid().ToString(), "tenant-a");
        var company = await AddCompanyAsync(context);
        context.Branches.Add(new Branch
        {
            ExternalId = "branch-1", CompanyId = company.Id, Code = "BR-1", Name = "Main",
            TimeZoneId = "Asia/Qatar"
        });
        await context.SaveChangesAsync();
        var csv = "external_id,branch_external_id,name,address\n"
            + "location-1,branch-1,Warehouse A,\n"
            + "location-2,missing,Warehouse B,";

        var result = await CreateService(context).ImportLocationsAsync(
            new ImportLocationsRequest(csv, false, company.Id));

        result.Created.Should().Be(1);
        result.Rejected.Should().Be(1);
        context.Locations.Should().BeEmpty();
    }

    [Fact]
    public async Task ImportSuppliers_replays_and_item_can_resolve_supplier_external_id()
    {
        await using var context = CreateContext(Guid.NewGuid().ToString(), "tenant-a");
        var company = await AddCompanyAsync(context);
        var supplierCsv = "external_id,name,contact_person,phone,email,address\n"
            + "supplier-1,Acme Supplies,Buyer,+97400000000,buyer@example.test,Doha";
        var supplierResult = await CreateService(context).ImportSuppliersAsync(
            new ImportSuppliersRequest(supplierCsv, false, company.Id));
        var replayed = await CreateService(context).ImportSuppliersAsync(
            new ImportSuppliersRequest(supplierCsv, false, company.Id));

        supplierResult.Created.Should().Be(1);
        replayed.Unchanged.Should().Be(1);
        var itemCsv = CsvWithSupplier("item-1,SKU-1,Widget,12.50,,,,1,1,2,false,supplier-1");
        var itemResult = await CreateService(context).ImportItemsAsync(
            new ImportItemsRequest(itemCsv, false, company.Id));

        itemResult.Created.Should().Be(1);
        (await context.Items.SingleAsync()).SupplierId.Should().Be(await context.Suppliers.Select(s => s.Id).SingleAsync());
    }

    [Fact]
    public async Task ImportItems_rejects_a_company_scope_from_another_tenant()
    {
        var databaseName = Guid.NewGuid().ToString();
        int foreignCompanyId;
        await using (var foreignContext = CreateContext(databaseName, "tenant-b"))
        {
            foreignCompanyId = (await AddCompanyAsync(foreignContext)).Id;
        }

        await using var context = CreateContext(databaseName, "tenant-a");
        var action = () => CreateService(context).ImportItemsAsync(new ImportItemsRequest(
            Csv("item-1,SKU-1,Widget,12.50,,,,1,1,2,false"), false, foreignCompanyId));

        await action.Should().ThrowAsync<ArgumentException>();
        context.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task ImportItems_creates_and_replays_without_duplicates()
    {
        await using var context = CreateContext(Guid.NewGuid().ToString(), "tenant-a");
        context.UnitsOfMeasure.Add(new UnitOfMeasure { ExternalId = "kg", Code = "KG", Name = "Kilogram" });
        await context.SaveChangesAsync();
        var service = CreateService(context);
        var csv = Csv("item-1,SKU-1,\"Blue widget, 10mm\",12.50,kg,,,1,1,2,false");

        var created = await service.ImportItemsAsync(new ImportItemsRequest(csv, DryRun: false));
        var replayed = await service.ImportItemsAsync(new ImportItemsRequest(csv, DryRun: false));

        created.Created.Should().Be(1);
        created.Unchanged.Should().Be(0);
        replayed.Created.Should().Be(0);
        replayed.Unchanged.Should().Be(1);
        context.Items.Count().Should().Be(1);
        (await context.Items.SingleAsync()).Description.Should().Be("Blue widget, 10mm");
    }

    [Fact]
    public async Task ImportItems_dry_run_and_rejected_row_do_not_mutate()
    {
        var databaseName = Guid.NewGuid().ToString();
        await using (var unitContext = CreateContext(databaseName, "tenant-a"))
        {
            unitContext.UnitsOfMeasure.Add(new UnitOfMeasure { ExternalId = "kg", Code = "KG", Name = "Kilogram" });
            await unitContext.SaveChangesAsync();
        }

        await using var context = CreateContext(databaseName, "tenant-a");
        var service = CreateService(context);
        var validRow = "item-1,SKU-1,Widget,12.50,kg,,,1,1,2,false";
        var invalidRow = "item-2,SKU-2,Widget,not-a-rate,kg,,,1,1,2,false";

        var dryRun = await service.ImportItemsAsync(new ImportItemsRequest(Csv(validRow), DryRun: true));
        var rejected = await service.ImportItemsAsync(new ImportItemsRequest(Csv(validRow, invalidRow), DryRun: false));

        dryRun.Created.Should().Be(1);
        context.Items.Should().BeEmpty();
        rejected.Created.Should().Be(1);
        rejected.Rejected.Should().Be(1);
        context.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task ImportItems_rejects_unit_from_another_tenant()
    {
        var databaseName = Guid.NewGuid().ToString();
        await using (var foreignContext = CreateContext(databaseName, "tenant-b"))
        {
            foreignContext.UnitsOfMeasure.Add(new UnitOfMeasure { ExternalId = "kg", Code = "KG", Name = "Kilogram" });
            await foreignContext.SaveChangesAsync();
        }

        await using var context = CreateContext(databaseName, "tenant-a");
        var result = await CreateService(context).ImportItemsAsync(new ImportItemsRequest(
            Csv("item-1,SKU-1,Widget,12.50,kg,,,1,1,2,false"), DryRun: false));

        result.Rejected.Should().Be(1);
        result.Rows.Single().Error.Should().Contain("current tenant");
        context.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task ImportUnits_rejects_fractional_precision_for_whole_only_units()
    {
        await using var context = CreateContext(Guid.NewGuid().ToString(), "tenant-a");
        var result = await CreateService(context).ImportUnitsAsync(new ImportUnitsRequest(
            "external_id,code,name,decimal_places,whole_unit_only\nbox,BOX,Box,2,true", DryRun: false));

        result.Rejected.Should().Be(1);
        result.Rows.Single().Error.Should().Be("Whole-unit-only units must use zero decimal places.");
        context.UnitsOfMeasure.Should().BeEmpty();
    }

    [Fact]
    public async Task ImportItems_rejects_non_identity_factor_for_a_base_unit_reference()
    {
        await using var context = CreateContext(Guid.NewGuid().ToString(), "tenant-a");
        context.UnitsOfMeasure.Add(new UnitOfMeasure { ExternalId = "piece", Code = "PC", Name = "Piece" });
        await context.SaveChangesAsync();

        var result = await CreateService(context).ImportItemsAsync(new ImportItemsRequest(
            Csv("item-1,SKU-1,Widget,12.50,piece,piece,,12,1,0,false"), DryRun: false));

        result.Rejected.Should().Be(1);
        result.Rows.Single().Error.Should().Contain(
            "Purchase-to-base factor must be 1 when the purchase unit is the base unit.");
        context.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task ImportItems_rejects_a_factor_that_would_round_in_postgresql()
    {
        await using var context = CreateContext(Guid.NewGuid().ToString(), "tenant-a");
        var result = await CreateService(context).ImportItemsAsync(new ImportItemsRequest(
            Csv("item-1,SKU-1,Widget,12.50,,,,1.0000001,1,0,false"), DryRun: false));

        result.Rejected.Should().Be(1);
        result.Rows.Single().Error.Should().Contain("must fit decimal(18,6) without rounding");
        context.Items.Should().BeEmpty();
    }

    private static MasterDataImportService CreateService(InventoryDbContext context) => new(
        new Repository<UnitOfMeasure>(context),
        new Repository<Item>(context),
        new Repository<Company>(context),
        new Repository<Branch>(context),
        new Repository<Location>(context),
        new Repository<Supplier>(context),
        context,
        new UnitOfWork(context));

    private static InventoryDbContext CreateContext(string databaseName, string tenantId) =>
        new(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options, new TestTenantContext(tenantId));

    private static string Csv(params string[] rows) =>
        "external_id,item_code,description,rate,base_unit_external_id,purchase_unit_external_id,sales_unit_external_id,purchase_to_base_factor,sales_to_base_factor,quantity_precision,whole_unit_only\n"
        + string.Join('\n', rows);

    private static string CsvWithSupplier(params string[] rows) =>
        "external_id,item_code,description,rate,base_unit_external_id,purchase_unit_external_id,sales_unit_external_id,purchase_to_base_factor,sales_to_base_factor,quantity_precision,whole_unit_only,supplier_external_id\n"
        + string.Join('\n', rows);

    private static async Task<Company> AddCompanyAsync(InventoryDbContext context)
    {
        var company = new Company
        {
            Code = $"C-{Guid.NewGuid():N}"[..10],
            LegalName = "Test company",
            BaseCurrency = "QAR",
            CurrencyScale = 2
        };
        context.Companies.Add(company);
        await context.SaveChangesAsync();
        return company;
    }
}
