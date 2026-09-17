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
    public async Task ImportUnits_parses_bom_and_quoted_fields()
    {
        await using var context = CreateContext(Guid.NewGuid().ToString(), "tenant-a");
        const string csv = "\uFEFFexternal_id,code,name,decimal_places,whole_unit_only\r\n"
            + "unit-1,BOX,\"Box, \"\"large\"\"\r\ncrate\",0,false";

        var result = await CreateService(context).ImportUnitsAsync(new ImportUnitsRequest(csv, DryRun: false));

        result.Created.Should().Be(1);
        result.Rejected.Should().Be(0);
        (await context.UnitsOfMeasure.SingleAsync()).Name.Should().Be("Box, \"large\"\r\ncrate");
    }

    [Fact]
    public async Task ImportUnits_rejects_invalid_decimal_places_and_boolean_without_mutation()
    {
        await using var context = CreateContext(Guid.NewGuid().ToString(), "tenant-a");
        const string csv = "external_id,code,name,decimal_places,whole_unit_only\n"
            + "unit-1,EA,Each,not-an-integer,false\n"
            + "unit-2,BOX,Box,0,maybe";

        var result = await CreateService(context).ImportUnitsAsync(new ImportUnitsRequest(csv, DryRun: false));

        result.Created.Should().Be(0);
        result.Rejected.Should().Be(2);
        result.Rows[0].Error.Should().Be("Decimal places must be an integer between 0 and 6.");
        result.Rows[1].Error.Should().Be("Whole-unit-only must be true or false.");
        context.UnitsOfMeasure.Should().BeEmpty();
    }

    [Fact]
    public async Task ImportUnits_rejects_malformed_quoted_row_without_importing_valid_rows()
    {
        await using var context = CreateContext(Guid.NewGuid().ToString(), "tenant-a");
        const string csv = "external_id,code,name,decimal_places,whole_unit_only\n"
            + "unit-1,EA,Each,0,false\n"
            + "unit-2,BOX,\"unterminated,0,false";

        var result = await CreateService(context).ImportUnitsAsync(new ImportUnitsRequest(csv, DryRun: false));

        result.Created.Should().Be(1);
        result.Rejected.Should().Be(1);
        result.Rows[1].RowNumber.Should().Be(3);
        result.Rows[1].Error.Should().Be("A quoted CSV field is not closed.");
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
        new UnitOfWork(context));

    private static InventoryDbContext CreateContext(string databaseName, string tenantId) =>
        new(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options, new TestTenantContext(tenantId));

    private static string Csv(params string[] rows) =>
        "external_id,item_code,description,rate,base_unit_external_id,purchase_unit_external_id,sales_unit_external_id,purchase_to_base_factor,sales_to_base_factor,quantity_precision,whole_unit_only\n"
        + string.Join('\n', rows);
}
