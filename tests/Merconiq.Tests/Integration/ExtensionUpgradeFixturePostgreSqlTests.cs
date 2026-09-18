using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using MediatR;
using Merconiq.Core.Entities;
using Merconiq.Core.Features.Items.Commands;
using Merconiq.Infrastructure.Data;
using Merconiq.Tests.Infrastructure;
using Merconiq.Web.Security;
using Merconiq.Web.Tenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class ExtensionUpgradeFixturePostgreSqlTests(PostgreSqlIntegrationFixture fixture)
{
    private const string PredecessorMigration = "20260918183139_EnforceAuditLogAppendOnly";
    private const string TargetMigration = "20260918200446_AddCompanyScopedCustomers";

    [PostgreSqlFact]
    public async Task Compiled_command_adapter_runs_after_upgrade_without_bypassing_auth_audit_or_other_ledgers()
    {
        fixture.EnsureEnabled();

        var suffix = Guid.NewGuid().ToString("N");
        var schemaName = $"extension_upgrade_{suffix}";
        var tenantId = $"extension-{suffix}";
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            SearchPath = schemaName
        }.ConnectionString;

        await using (var createSchema = new NpgsqlConnection(fixture.ConnectionString))
        {
            await createSchema.OpenAsync();
            await using var command = createSchema.CreateCommand();
            command.CommandText = $"CREATE SCHEMA \"{schemaName}\"";
            await command.ExecuteNonQueryAsync();
        }

        try
        {
            var options = CreateOptions(connectionString);
            int existingItemId;
            await using (var predecessor = new InventoryDbContext(options, new TestTenantContext(tenantId)))
            {
                await predecessor.Database.MigrateAsync(PredecessorMigration);
                var existingItem = new Item
                {
                    ItemCode = $"PRE-{suffix[..12]}",
                    Description = "Synthetic item created before the upgrade",
                    Rate = 12.50m
                };
                predecessor.Items.Add(existingItem);
                await predecessor.SaveChangesAsync();
                existingItemId = existingItem.Id;
            }

            await using (var upgraded = new InventoryDbContext(options, new TestTenantContext(tenantId)))
            {
                await upgraded.Database.MigrateAsync();
                (await upgraded.Database.GetAppliedMigrationsAsync()).Last().Should().Be(TargetMigration);
                (await upgraded.Items.SingleAsync(item => item.Id == existingItemId))
                    .Description.Should().Be("Synthetic item created before the upgrade");
            }

            var adapterProbe = new ItemCommandAdapterProbe();
            using var factory = new ExtensionUpgradeApiFactory(connectionString, tenantId, suffix, adapterProbe);
            using var anonymousClient = factory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var itemCode = $"EXT-{suffix[..12]}";
            var request = new
            {
                itemCode,
                description = "Synthetic item created by the compiled adapter fixture",
                rate = 18.75m
            };
            var denied = await anonymousClient.PostAsJsonAsync("/api/v1/items", request);

            denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            adapterProbe.ObservedItemCodes.Should().BeEmpty(
                "the existing API authentication/authorization pipeline must run before the MediatR adapter");

            using var staffClient = await factory.CreateAuthenticatedClientAsync("Staff");
            var forbidden = await staffClient.PostAsJsonAsync("/api/v1/items", request);
            forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            adapterProbe.ObservedItemCodes.Should().BeEmpty(
                "an authenticated caller without Edit capability must not reach the adapter");

            LedgerSnapshot before;
            await using (var verification = new InventoryDbContext(options, new TestTenantContext(tenantId)))
            {
                before = await ReadLedgerSnapshotAsync(verification);
                (await verification.Items.CountAsync(item => item.ItemCode == itemCode)).Should().Be(0);
                (await verification.AuditLogs.CountAsync(log =>
                    log.NewValues != null && log.NewValues.Contains(itemCode))).Should().Be(0);
            }

            using var authenticatedClient = await factory.CreateAuthenticatedClientAsync();
            var created = await authenticatedClient.PostAsJsonAsync("/api/v1/items", request);
            created.StatusCode.Should().Be(HttpStatusCode.Created);
            adapterProbe.ObservedItemCodes.Should().Equal(itemCode);

            await using (var verification = new InventoryDbContext(options, new TestTenantContext(tenantId)))
            {
                (await verification.Items.CountAsync(item => item.ItemCode == itemCode)).Should().Be(1);

                var audit = await verification.AuditLogs.SingleAsync(log =>
                    log.EntityName == nameof(Item) &&
                    log.Action == "Insert" &&
                    log.NewValues!.Contains(itemCode));
                audit.TenantId.Should().Be(tenantId);
                audit.Username.Should().Be("extension-admin-" + suffix + "@example.test");

                (await ReadLedgerSnapshotAsync(verification)).Should().Be(before,
                    "the catalog adapter must not write stock, valuation, procurement, or transit ledgers");
            }
        }
        finally
        {
            await using var dropSchema = new NpgsqlConnection(fixture.ConnectionString);
            await dropSchema.OpenAsync();
            await using var command = dropSchema.CreateCommand();
            command.CommandText = $"DROP SCHEMA IF EXISTS \"{schemaName}\" CASCADE";
            await command.ExecuteNonQueryAsync();
        }
    }

    private static DbContextOptions<InventoryDbContext> CreateOptions(string connectionString) =>
        new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql(connectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

    private static async Task<LedgerSnapshot> ReadLedgerSnapshotAsync(InventoryDbContext context) => new(
        await context.StockInHand.CountAsync(),
        await context.StockTransactions.CountAsync(),
        await context.StockValuationBuckets.CountAsync(),
        await context.StockValuationEntries.CountAsync(),
        await context.PurchaseOrders.CountAsync(),
        await context.TransferTransitEntries.CountAsync(),
        await context.TransferTransitSettlements.CountAsync());

    private sealed record LedgerSnapshot(
        int StockPositions,
        int StockMovements,
        int ValuationBuckets,
        int ValuationEntries,
        int PurchaseOrders,
        int TransitEntries,
        int TransitSettlements);
}

/// <summary>Test-only compile-time MediatR adapter registered through the existing host DI seam.</summary>
internal sealed class ItemCommandAuditAdapter(ItemCommandAdapterProbe probe)
    : IPipelineBehavior<CreateItemCommand, Item>
{
    public async Task<Item> Handle(
        CreateItemCommand request,
        RequestHandlerDelegate<Item> next,
        CancellationToken cancellationToken)
    {
        var item = await next(cancellationToken);
        probe.Record(item.ItemCode);
        return item;
    }
}

internal sealed class ItemCommandAdapterProbe
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _observedItemCodes = new();

    public IReadOnlyList<string> ObservedItemCodes => _observedItemCodes.ToArray();

    public void Record(string itemCode) => _observedItemCodes.Enqueue(itemCode);
}

internal sealed class ExtensionUpgradeApiFactory(
    string connectionString,
    string tenantId,
    string suffix,
    ItemCommandAdapterProbe adapterProbe) : WebApplicationFactory<Merconiq.Web.Program>
{
    private const string TestJwtSecret = "testing-only-jwt-secret-with-at-least-32-bytes";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("JwtSettings:Secret", TestJwtSecret);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.AddDbContext<InventoryDbContext>(options => options.UseNpgsql(connectionString));
            services.AddScoped<TenantContext>(_ =>
            {
                var context = new TenantContext();
                context.SetTenant(tenantId);
                return context;
            });
            services.AddSingleton(adapterProbe);
            services.AddTransient<IPipelineBehavior<CreateItemCommand, Item>, ItemCommandAuditAdapter>();
        });
    }

    public async Task<HttpClient> CreateAuthenticatedClientAsync(string role = "Admin")
    {
        var user = await EnsureRoleUserAsync(role);
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var tokenHandler = new JwtSecurityTokenHandler();
        var token = tokenHandler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(JwtClaimsFactory.Create(user, tenantId, [role])),
            Expires = DateTime.UtcNow.AddMinutes(5),
            Issuer = "Merconiq",
            Audience = "Merconiq",
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret)),
                SecurityAlgorithms.HmacSha256Signature)
        });
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenHandler.WriteToken(token));
        return client;
    }

    private async Task<ApplicationUser> EnsureRoleUserAsync(string role)
    {
        using var scope = Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var userId = $"extension-{role.ToLowerInvariant()}-{suffix}";
        var userName = $"extension-{role.ToLowerInvariant()}-{suffix}@example.test";
        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            user = new ApplicationUser
            {
                Id = userId,
                UserName = userName,
                Email = userName,
                EmailConfirmed = true,
                TenantId = tenantId
            };
            var createResult = await userManager.CreateAsync(user);
            if (!createResult.Succeeded)
            {
                throw new InvalidOperationException(string.Join("; ", createResult.Errors.Select(error => error.Code)));
            }
        }

        if (!await roleManager.RoleExistsAsync(role))
        {
            var createRole = await roleManager.CreateAsync(new IdentityRole(role));
            if (!createRole.Succeeded)
            {
                throw new InvalidOperationException(string.Join("; ", createRole.Errors.Select(error => error.Code)));
            }
        }

        if (!await userManager.IsInRoleAsync(user, role))
        {
            var addRole = await userManager.AddToRoleAsync(user, role);
            if (!addRole.Succeeded)
            {
                throw new InvalidOperationException(string.Join("; ", addRole.Errors.Select(error => error.Code)));
            }
        }

        return (await userManager.FindByIdAsync(userId))!;
    }
}
