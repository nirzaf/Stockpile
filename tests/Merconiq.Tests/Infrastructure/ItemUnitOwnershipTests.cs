using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Tests.Infrastructure;

public sealed class ItemUnitOwnershipTests
{
    [Fact]
    public void Item_unit_relationships_include_tenant_ownership()
    {
        using var context = new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new TestTenantContext("tenant-a"));
        var item = context.Model.FindEntityType(typeof(Item))!;
        var unit = context.Model.FindEntityType(typeof(UnitOfMeasure))!;

        unit.GetIndexes().Should().Contain(index =>
            index.IsUnique && index.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { nameof(UnitOfMeasure.Id), nameof(UnitOfMeasure.TenantId) }));
        item.GetForeignKeys().Should().Contain(foreignKey =>
            foreignKey.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { nameof(Item.BaseUnitId), nameof(Item.TenantId) }) &&
            foreignKey.PrincipalEntityType.ClrType == typeof(UnitOfMeasure));
    }
}
