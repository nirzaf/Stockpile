using System.Net;
using System.Text.Json;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Merconiq.Tests.Integration;

public sealed class ItemMasterExportApiTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    [Fact]
    public async Task Export_is_tenant_admin_only_bounded_and_redacts_sensitive_fields()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var realUnit = Unit($"unit-real-{suffix}", $"UR{suffix[..8]}", "Real unit");
        var legacyUnit = Unit(
            MasterDataImportConventions.LegacyUnmappedUnitExternalIdPrefix + suffix,
            $"UL{suffix[..8]}",
            "Legacy unit with unknown source ID");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            context.UnitsOfMeasure.AddRange(realUnit, legacyUnit);
            context.Items.AddRange(
                new Item
                {
                    TenantId = "test-tenant",
                    ExternalId = $"item-a-{suffix}",
                    ItemCode = $"IA{suffix[..8]}",
                    Description = "Exportable item A",
                    Barcode = $"sensitive-barcode-{suffix}",
                    Rate = 9876.54m,
                    BaseUnit = realUnit,
                    PurchaseUnit = legacyUnit,
                    SalesUnit = realUnit,
                    PurchaseToBaseFactor = 12m,
                    SalesToBaseFactor = 0.5m,
                    QuantityPrecision = 3,
                    WholeUnitOnly = false,
                    IsActive = true
                },
                new Item
                {
                    TenantId = "test-tenant",
                    ExternalId = $"item-b-{suffix}",
                    ItemCode = $"IB{suffix[..8]}",
                    Description = "Exportable item B",
                    BaseUnit = legacyUnit,
                    WholeUnitOnly = true
                },
                new Item
                {
                    TenantId = "test-tenant",
                    ExternalId = null,
                    ItemCode = $"IN{suffix[..8]}",
                    Description = "Legacy item with unknown source ID"
                },
                new Item
                {
                    TenantId = "test-tenant",
                    ExternalId = string.Empty,
                    ItemCode = $"IE{suffix[..8]}",
                    Description = "Item with blank source ID"
                },
                new Item
                {
                    TenantId = "test-tenant",
                    ExternalId = "   ",
                    ItemCode = $"IW{suffix[..8]}",
                    Description = "Item with whitespace source ID"
                },
                new Item
                {
                    TenantId = "test-tenant",
                    ExternalId = $"item-deleted-{suffix}",
                    ItemCode = $"ID{suffix[..8]}",
                    Description = "Soft-deleted item",
                    IsDeleted = true
                });
            await context.SaveChangesAsync();
        }

        using var anonymous = factory.CreateUnauthenticatedClient();
        using var unauthenticated = await anonymous.GetAsync("/api/v1/organization/items/export");
        unauthenticated.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var companyOperator = factory.CreateAuthenticatedClient("Buyer");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var context = services.GetRequiredService<InventoryDbContext>();
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var buyer = (await users.FindByNameAsync("buyer@test-tenant.test"))!;
            var company = new Company
            {
                TenantId = "test-tenant",
                Code = $"EXP-{Guid.NewGuid():N}"[..12],
                LegalName = "Export test company",
                BaseCurrency = "QAR",
                CurrencyScale = 2
            };
            context.Companies.Add(company);
            await context.SaveChangesAsync();
            context.CompanyMemberships.Add(new CompanyMembership
            {
                TenantId = "test-tenant",
                CompanyId = company.Id,
                UserId = buyer.Id,
                Capabilities = CompanyCapability.View | CompanyCapability.Edit | CompanyCapability.Administer,
                IsActive = true
            });
            await context.SaveChangesAsync();
        }

        using var forbidden = await companyOperator.GetAsync("/api/v1/organization/items/export");
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "items are currently tenant-scoped and company ownership is not defined");

        using var admin = factory.CreateAuthenticatedClient("Admin");
        using var zeroPageSize = await admin.GetAsync("/api/v1/organization/items/export?pageSize=0");
        zeroPageSize.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var excessivePageSize = await admin.GetAsync("/api/v1/organization/items/export?pageSize=101");
        excessivePageSize.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var oversizedCursor = await admin.GetAsync(
            $"/api/v1/organization/items/export?afterExternalId={new string('x', 129)}");
        oversizedCursor.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var firstResponse = await admin.GetAsync("/api/v1/organization/items/export?pageSize=1");
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var firstJson = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        var firstPage = firstJson.RootElement.GetProperty("data");
        firstPage.GetProperty("pageSize").GetInt32().Should().Be(1);
        firstPage.GetProperty("hasMore").GetBoolean().Should().BeTrue();
        firstPage.GetProperty("nextCursor").GetString().Should().Be($"item-a-{suffix}");
        var firstItem = firstPage.GetProperty("items").EnumerateArray().Single();
        firstItem.GetProperty("externalId").GetString().Should().Be($"item-a-{suffix}");
        firstItem.GetProperty("itemCode").GetString().Should().Be($"IA{suffix[..8]}");
        firstItem.GetProperty("description").GetString().Should().Be("Exportable item A");
        firstItem.GetProperty("baseUnitExternalId").GetString().Should().Be(realUnit.ExternalId);
        firstItem.GetProperty("purchaseUnitExternalId").ValueKind.Should().Be(JsonValueKind.Null,
            "synthetic legacy unit IDs do not identify a source-system unit");
        firstItem.GetProperty("salesUnitExternalId").GetString().Should().Be(realUnit.ExternalId);
        firstItem.GetProperty("purchaseToBaseFactor").GetDecimal().Should().Be(12m);
        firstItem.GetProperty("salesToBaseFactor").GetDecimal().Should().Be(0.5m);
        firstItem.GetProperty("quantityPrecision").GetInt32().Should().Be(3);
        firstItem.GetProperty("wholeUnitOnly").GetBoolean().Should().BeFalse();
        firstItem.GetProperty("isActive").GetBoolean().Should().BeTrue();
        firstItem.EnumerateObject().Select(property => property.Name).Should().Equal(
            "externalId", "itemCode", "description", "baseUnitExternalId", "purchaseUnitExternalId",
            "salesUnitExternalId", "purchaseToBaseFactor", "salesToBaseFactor", "quantityPrecision",
            "wholeUnitOnly", "isActive");
        foreach (var omittedField in new[]
                 { "id", "tenantId", "companyId", "rate", "sellingPrice", "supplierId", "barcode", "reorderLevel" })
        {
            firstItem.TryGetProperty(omittedField, out _).Should().BeFalse();
        }

        using var secondResponse = await admin.GetAsync(
            $"/api/v1/organization/items/export?afterExternalId=item-a-{suffix}&pageSize=1");
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var secondJson = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync());
        var secondPage = secondJson.RootElement.GetProperty("data");
        secondPage.GetProperty("hasMore").GetBoolean().Should().BeFalse();
        secondPage.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
        var secondItem = secondPage.GetProperty("items").EnumerateArray().Single();
        secondItem.GetProperty("externalId").GetString().Should().Be($"item-b-{suffix}");
        secondItem.GetProperty("baseUnitExternalId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private static UnitOfMeasure Unit(string externalId, string code, string name) => new()
    {
        TenantId = "test-tenant",
        ExternalId = externalId,
        Code = code,
        Name = name,
        DecimalPlaces = 3,
        IsWholeUnitOnly = false
    };
}

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class ItemMasterExportPostgreSqlApiTests(PostgreSqlIntegrationFixture fixture) : IDisposable
{
    private readonly PostgreSqlCompanyApiFactory _factory = new(
        fixture, applicationName: "merconiq-item-export-tenant-isolation-api");

    [PostgreSqlFact]
    public async Task Export_is_limited_to_the_authenticated_tenant()
    {
        fixture.EnsureEnabled();
        var suffix = Guid.NewGuid().ToString("N");
        await using (var currentTenant = fixture.CreateContext("test-tenant"))
        {
            currentTenant.Items.Add(Item("test-tenant", $"visible-{suffix}", $"VA{suffix[..8]}"));
            await currentTenant.SaveChangesAsync();
        }

        await using (var otherTenant = fixture.CreateContext("another-tenant"))
        {
            otherTenant.Items.Add(Item("another-tenant", $"hidden-{suffix}", $"HB{suffix[..8]}"));
            await otherTenant.SaveChangesAsync();
        }

        var adminUser = await _factory.EnsurePersonaUserAsync("Admin", suffix);
        using var admin = _factory.CreateAuthenticatedClient(adminUser, "Admin");
        using var response = await admin.GetAsync("/api/v1/organization/items/export");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = json.RootElement.GetProperty("data").GetProperty("items").EnumerateArray().ToList();
        items.Should().ContainSingle(item => item.GetProperty("externalId").GetString() == $"visible-{suffix}");
        items.Should().NotContain(item => item.GetProperty("externalId").GetString() == $"hidden-{suffix}");
    }

    public void Dispose() => _factory.Dispose();

    private static Item Item(string tenantId, string externalId, string itemCode) => new()
    {
        TenantId = tenantId,
        ExternalId = externalId,
        ItemCode = itemCode,
        Description = "Tenant-isolated item",
        IsActive = true
    };
}
