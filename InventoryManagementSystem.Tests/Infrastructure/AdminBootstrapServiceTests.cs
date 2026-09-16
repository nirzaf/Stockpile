using FluentAssertions;
using InventoryManagementSystem.Core.Entities;
using InventoryManagementSystem.Core.Interfaces;
using InventoryManagementSystem.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryManagementSystem.Tests.Infrastructure;

public class AdminBootstrapServiceTests
{
    [Fact]
    public async Task Bootstrap_is_tenant_bound_and_safe_to_repeat()
    {
        var databaseName = Guid.NewGuid().ToString();
        await using (var tenantA = CreateProvider(databaseName, "tenant-a"))
        {
            using var scope = tenantA.CreateScope();
            var bootstrapper = scope.ServiceProvider.GetRequiredService<AdminBootstrapService>();

            await bootstrapper.BootstrapAsync("tenant-a", "admin@example.test", "ValidPassword1");
            await bootstrapper.BootstrapAsync("tenant-a", "admin@example.test", "ValidPassword1");

            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            var user = await db.Users.SingleAsync();
            user.TenantId.Should().Be("tenant-a");
            (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
                    .GetRolesAsync(user))
                .Should().ContainSingle(AdminBootstrapService.AdministratorRole);
        }

        await using (var tenantB = CreateProvider(databaseName, "tenant-b"))
        {
            using var scope = tenantB.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            (await db.Users.CountAsync()).Should().Be(0);
        }
    }

    [Fact]
    public async Task Bootstrap_reports_identity_failures_and_does_not_create_a_partial_user()
    {
        await using var provider = CreateProvider(Guid.NewGuid().ToString(), "tenant-a");
        using var scope = provider.CreateScope();
        var bootstrapper = scope.ServiceProvider.GetRequiredService<AdminBootstrapService>();

        var act = () => bootstrapper.BootstrapAsync("tenant-a", "admin@example.test", "weak");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Creating the administrator failed:*");
        (await scope.ServiceProvider.GetRequiredService<InventoryDbContext>().Users.CountAsync())
            .Should().Be(0);
    }

    [Fact]
    public async Task Bootstrap_rejects_an_invalid_tenant_identifier()
    {
        await using var provider = CreateProvider(Guid.NewGuid().ToString(), "tenant-a");
        using var scope = provider.CreateScope();
        var bootstrapper = scope.ServiceProvider.GetRequiredService<AdminBootstrapService>();

        var act = () => bootstrapper.BootstrapAsync("tenant/a", "admin@example.test", "ValidPassword1");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*tenant identifier*");
    }

    private static ServiceProvider CreateProvider(string databaseName, string tenantId)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(_ => new TestTenantContext(tenantId));
        services.AddDbContext<InventoryDbContext>(options => options.UseInMemoryDatabase(databaseName));
        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredLength = 8;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<InventoryDbContext>();
        services.AddScoped<AdminBootstrapService>();
        return services.BuildServiceProvider();
    }
}
