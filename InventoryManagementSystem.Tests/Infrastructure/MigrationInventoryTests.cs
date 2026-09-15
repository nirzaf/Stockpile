using FluentAssertions;
using InventoryManagementSystem.Core.Entities;
using InventoryManagementSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Migrations;

namespace InventoryManagementSystem.Tests.Infrastructure;

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
                "InventoryManagementSystem.Infrastructure.Migrations.InventoryDbContextModelSnapshot")
            .Should()
            .NotBeNull();

        typeof(ApplicationUser).Namespace.Should().Be("InventoryManagementSystem.Core.Entities");
        typeof(ApplicationUser).Assembly.GetName().Name.Should().Be("InventoryManagementSystem.Core");
    }
}
