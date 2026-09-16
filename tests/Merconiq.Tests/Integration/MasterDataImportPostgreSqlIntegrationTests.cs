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
        new UnitOfWork(context));
}
