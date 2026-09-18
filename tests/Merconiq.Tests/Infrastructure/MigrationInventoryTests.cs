using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Merconiq.Tests.Infrastructure;

public class MigrationInventoryTests
{
    [Fact]
    public void Infrastructure_contains_initial_migration_and_model_snapshot()
    {
        var infrastructureAssembly = typeof(InventoryDbContext).Assembly;
        var migrationTypes = infrastructureAssembly.GetTypes()
            .Where(type => typeof(Migration).IsAssignableFrom(type))
            .Select(type => type.Name)
            .ToArray();

        migrationTypes.Should().Contain("InitialCreate");
        infrastructureAssembly.GetType(
                "Merconiq.Infrastructure.Migrations.InventoryDbContextModelSnapshot")
            .Should()
            .NotBeNull();
        infrastructureAssembly.GetType(
                "Merconiq.Infrastructure.Migrations.AddTenantIsolation")
            .Should()
            .NotBeNull();
        infrastructureAssembly.GetTypes()
            .Where(type => typeof(Migration).IsAssignableFrom(type))
            .Select(type => type.Name)
            .Should()
            .Contain("AddCompanyScopedCustomers");

        typeof(ApplicationUser).Namespace.Should().Be("Merconiq.Core.Entities");
        typeof(ApplicationUser).Assembly.GetName().Name.Should().Be("Merconiq.Core");
    }
}
