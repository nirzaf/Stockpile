using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Merconiq.Tests.Infrastructure;

public class StockConcurrencyTests
{
    [Fact]
    public void StockInHand_UsesPostgresXminAsConcurrencyToken()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var context = new InventoryDbContext(options, new TestTenantContext("default"));
        var version = context.Model
            .FindEntityType(typeof(StockInHand))!
            .FindProperty("Version");

        version.Should().NotBeNull();
        version!.IsConcurrencyToken.Should().BeTrue();
        version.ValueGenerated.Should().Be(ValueGenerated.OnAddOrUpdate);
        version.GetColumnName().Should().Be("xmin");
    }
}
