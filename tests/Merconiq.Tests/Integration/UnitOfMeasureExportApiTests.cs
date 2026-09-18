using System.Net;
using System.Text.Json;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Merconiq.Tests.Integration;

public sealed class UnitOfMeasureExportApiTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    [Fact]
    public async Task Export_is_admin_only_bounded_and_returns_deterministic_source_fields()
    {
        var suffix = Guid.NewGuid().ToString("N");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            context.UnitsOfMeasure.AddRange(
                Unit("test-tenant", $"unit-z-{suffix}", $"UZ{suffix[..8]}", "Zulu unit", 3, false),
                Unit("test-tenant", $"unit-a-{suffix}", $"UA{suffix[..8]}", "Alpha unit", 2, true),
                Unit("test-tenant", string.Empty, $"UE{suffix[..8]}", "Unit without a source ID", 0, true),
                Unit("test-tenant", MasterDataImportConventions.LegacyUnmappedUnitExternalIdPrefix + suffix,
                    $"UL{suffix[..8]}", "Legacy unit with unknown source ID", 0, true));
            await context.SaveChangesAsync();
        }

        using var anonymous = factory.CreateUnauthenticatedClient();
        using var unauthenticated = await anonymous.GetAsync("/api/v1/organization/units/export");
        unauthenticated.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var buyer = factory.CreateAuthenticatedClient("Buyer");
        using var forbidden = await buyer.GetAsync("/api/v1/organization/units/export");
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var admin = factory.CreateAuthenticatedClient("Admin");
        using var zeroPageSize = await admin.GetAsync("/api/v1/organization/units/export?pageSize=0");
        zeroPageSize.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var excessivePageSize = await admin.GetAsync("/api/v1/organization/units/export?pageSize=101");
        excessivePageSize.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var oversizedCursor = await admin.GetAsync(
            $"/api/v1/organization/units/export?afterExternalId={new string('x', 129)}");
        oversizedCursor.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var firstResponse = await admin.GetAsync("/api/v1/organization/units/export?pageSize=1");
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var firstJson = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        var firstPage = firstJson.RootElement.GetProperty("data");
        firstPage.GetProperty("pageSize").GetInt32().Should().Be(1);
        firstPage.GetProperty("hasMore").GetBoolean().Should().BeTrue();
        firstPage.GetProperty("nextCursor").GetString().Should().Be($"unit-a-{suffix}");
        var firstUnit = firstPage.GetProperty("units").EnumerateArray().Single();
        firstUnit.GetProperty("externalId").GetString().Should().Be($"unit-a-{suffix}");
        firstUnit.GetProperty("code").GetString().Should().Be($"UA{suffix[..8]}");
        firstUnit.GetProperty("name").GetString().Should().Be("Alpha unit");
        firstUnit.GetProperty("decimalPlaces").GetInt32().Should().Be(2);
        firstUnit.GetProperty("isWholeUnitOnly").GetBoolean().Should().BeTrue();
        firstUnit.EnumerateObject().Select(property => property.Name).Should().Equal(
            "externalId", "code", "name", "decimalPlaces", "isWholeUnitOnly");
        firstUnit.TryGetProperty("id", out _).Should().BeFalse();
        firstUnit.TryGetProperty("tenantId", out _).Should().BeFalse();

        using var secondResponse = await admin.GetAsync(
            $"/api/v1/organization/units/export?afterExternalId=unit-a-{suffix}&pageSize=1");
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var secondJson = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync());
        var secondPage = secondJson.RootElement.GetProperty("data");
        secondPage.GetProperty("hasMore").GetBoolean().Should().BeFalse(
            "only the unit with a real source ID from this tenant should follow the first page; returned IDs were {0}",
            string.Join(", ", secondPage.GetProperty("units").EnumerateArray()
                .Select(unit => unit.GetProperty("externalId").GetString())));
        secondPage.GetProperty("units").EnumerateArray().Single()
            .GetProperty("externalId").GetString().Should().Be($"unit-z-{suffix}");
    }

    private static UnitOfMeasure Unit(
        string tenantId,
        string externalId,
        string code,
        string name,
        int decimalPlaces,
        bool wholeUnitOnly) => new()
    {
        TenantId = tenantId,
        ExternalId = externalId,
        Code = code,
        Name = name,
        DecimalPlaces = decimalPlaces,
        IsWholeUnitOnly = wholeUnitOnly
    };
}

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class UnitOfMeasureExportPostgreSqlApiTests(PostgreSqlIntegrationFixture fixture) : IDisposable
{
    private readonly PostgreSqlCompanyApiFactory _factory = new(
        fixture, applicationName: "merconiq-unit-export-tenant-isolation-api");

    [PostgreSqlFact]
    public async Task Export_query_is_limited_to_the_authenticated_tenant()
    {
        fixture.EnsureEnabled();
        var suffix = Guid.NewGuid().ToString("N");
        await using (var currentTenant = fixture.CreateContext("test-tenant"))
        {
            currentTenant.UnitsOfMeasure.Add(Unit(
                "test-tenant", $"visible-{suffix}", $"V{suffix[..10]}", "Current tenant unit"));
            await currentTenant.SaveChangesAsync();
        }

        await using (var otherTenant = fixture.CreateContext("another-tenant"))
        {
            otherTenant.UnitsOfMeasure.Add(Unit(
                "another-tenant", $"hidden-{suffix}", $"H{suffix[..10]}", "Other tenant unit"));
            await otherTenant.SaveChangesAsync();
        }

        var adminUser = await _factory.EnsurePersonaUserAsync("Admin", suffix);
        using var admin = _factory.CreateAuthenticatedClient(adminUser, "Admin");
        using var response = await admin.GetAsync("/api/v1/organization/units/export");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var units = json.RootElement.GetProperty("data").GetProperty("units").EnumerateArray().ToList();
        units.Should().ContainSingle(unit => unit.GetProperty("externalId").GetString() == $"visible-{suffix}");
        units.Should().NotContain(unit => unit.GetProperty("externalId").GetString() == $"hidden-{suffix}");
    }

    public void Dispose() => _factory.Dispose();

    private static UnitOfMeasure Unit(string tenantId, string externalId, string code, string name) => new()
    {
        TenantId = tenantId,
        ExternalId = externalId,
        Code = code,
        Name = name,
        DecimalPlaces = 0,
        IsWholeUnitOnly = true
    };
}
