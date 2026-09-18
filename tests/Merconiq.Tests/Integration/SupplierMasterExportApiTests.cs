using System.Net;
using System.Text.Json;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Merconiq.Tests.Integration;

public sealed class SupplierMasterExportApiTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    [Fact]
    public async Task Export_is_tenant_admin_only_bounded_keyset_paginated_and_redacted()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var prefix = $"zzzz-supplier-export-{suffix}";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            context.Suppliers.AddRange(
                Supplier(prefix + "-a", "Alpha supplier", suffix),
                Supplier(prefix + "-b", "Beta supplier", suffix),
                Supplier(null, "Supplier without source ID", suffix),
                Supplier(string.Empty, "Supplier with empty source ID", suffix),
                Supplier("   ", "Supplier with whitespace source ID", suffix),
                new Supplier
                {
                    TenantId = "test-tenant",
                    ExternalId = prefix + "-deleted",
                    Name = "Deleted supplier",
                    IsDeleted = true
                });
            await context.SaveChangesAsync();
        }

        using var anonymous = factory.CreateUnauthenticatedClient();
        using var unauthorized = await anonymous.GetAsync("/api/v1/organization/suppliers/export");
        unauthorized.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var buyer = factory.CreateAuthenticatedClient("Buyer");
        using var forbidden = await buyer.GetAsync("/api/v1/organization/suppliers/export");
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var admin = factory.CreateAuthenticatedClient("Admin");
        using var zeroPageSize = await admin.GetAsync("/api/v1/organization/suppliers/export?pageSize=0");
        zeroPageSize.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var excessivePageSize = await admin.GetAsync("/api/v1/organization/suppliers/export?pageSize=101");
        excessivePageSize.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var emptyCursor = await admin.GetAsync("/api/v1/organization/suppliers/export?afterExternalId=");
        emptyCursor.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var blankCursor = await admin.GetAsync("/api/v1/organization/suppliers/export?afterExternalId=%20%20");
        blankCursor.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var oversizedCursor = await admin.GetAsync(
            $"/api/v1/organization/suppliers/export?afterExternalId={new string('x', 129)}");
        oversizedCursor.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var firstResponse = await admin.GetAsync(
            $"/api/v1/organization/suppliers/export?afterExternalId={prefix}-0&pageSize=1");
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var firstJson = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        var firstPage = firstJson.RootElement.GetProperty("data");
        firstPage.GetProperty("pageSize").GetInt32().Should().Be(1);
        firstPage.GetProperty("hasMore").GetBoolean().Should().BeTrue();
        firstPage.GetProperty("nextCursor").GetString().Should().Be(prefix + "-a");
        var firstSupplier = firstPage.GetProperty("suppliers").EnumerateArray().Single();
        firstSupplier.GetProperty("externalId").GetString().Should().Be(prefix + "-a");
        firstSupplier.GetProperty("name").GetString().Should().Be("Alpha supplier");
        firstSupplier.EnumerateObject().Select(property => property.Name).Should().Equal("externalId", "name");

        using var secondResponse = await admin.GetAsync(
            $"/api/v1/organization/suppliers/export?afterExternalId={prefix}-a&pageSize=1");
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var secondJson = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync());
        var secondPage = secondJson.RootElement.GetProperty("data");
        secondPage.GetProperty("hasMore").GetBoolean().Should().BeFalse();
        secondPage.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
        var secondSupplier = secondPage.GetProperty("suppliers").EnumerateArray().Single();
        secondSupplier.GetProperty("externalId").GetString().Should().Be(prefix + "-b");
        secondSupplier.GetProperty("name").GetString().Should().Be("Beta supplier");

        using var fullResponse = await admin.GetAsync("/api/v1/organization/suppliers/export?pageSize=100");
        fullResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var fullJson = JsonDocument.Parse(await fullResponse.Content.ReadAsStringAsync());
        var fullSuppliers = fullJson.RootElement.GetProperty("data").GetProperty("suppliers").EnumerateArray().ToList();
        var exportedNames = fullSuppliers.Select(supplier => supplier.GetProperty("name").GetString()).ToList();
        exportedNames.Should().NotContain("Supplier without source ID");
        exportedNames.Should().NotContain("Supplier with empty source ID");
        exportedNames.Should().NotContain("Supplier with whitespace source ID");
        exportedNames.Should().NotContain("Deleted supplier");
        var serialized = await fullResponse.Content.ReadAsStringAsync();
        serialized.Should().NotContain("contactPerson").And.NotContain("phone").And.NotContain("email")
            .And.NotContain("address").And.NotContain("tenantId").And.NotContain("companyId")
            .And.NotContain($"Sensitive contact {suffix}")
            .And.NotContain($"Sensitive phone {suffix}")
            .And.NotContain($"sensitive-{suffix}@example.test")
            .And.NotContain($"Sensitive address {suffix}");
    }

    private static Supplier Supplier(string? externalId, string name, string suffix) => new()
    {
        TenantId = "test-tenant",
        ExternalId = externalId,
        Name = name,
        ContactPerson = $"Sensitive contact {suffix}",
        Phone = $"Sensitive phone {suffix}",
        Email = $"sensitive-{suffix}@example.test",
        Address = $"Sensitive address {suffix}"
    };
}

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class SupplierMasterExportPostgreSqlApiTests(PostgreSqlIntegrationFixture fixture) : IDisposable
{
    private readonly PostgreSqlCompanyApiFactory _factory = new(
        fixture, applicationName: "merconiq-supplier-export-tenant-isolation-api");

    [PostgreSqlFact]
    public async Task Export_query_is_limited_to_the_authenticated_tenant()
    {
        fixture.EnsureEnabled();
        var suffix = Guid.NewGuid().ToString("N");
        var sharedExternalId = $"shared-supplier-{suffix}";

        await using (var currentTenant = fixture.CreateContext("test-tenant"))
        {
            currentTenant.Suppliers.Add(Supplier(
                "test-tenant", sharedExternalId, $"Visible supplier {suffix}"));
            await currentTenant.SaveChangesAsync();
        }

        await using (var otherTenant = fixture.CreateContext("another-tenant"))
        {
            otherTenant.Suppliers.AddRange(
                Supplier("another-tenant", sharedExternalId, $"Other-tenant duplicate {suffix}"),
                Supplier("another-tenant", $"hidden-supplier-{suffix}", $"Other-tenant supplier {suffix}"));
            await otherTenant.SaveChangesAsync();
        }

        var adminUser = await _factory.EnsurePersonaUserAsync("Admin", suffix);
        using var admin = _factory.CreateAuthenticatedClient(adminUser, "Admin");
        using var response = await admin.GetAsync("/api/v1/organization/suppliers/export");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var suppliers = json.RootElement.GetProperty("data").GetProperty("suppliers").EnumerateArray().ToList();
        suppliers.Should().ContainSingle(supplier =>
            supplier.GetProperty("externalId").GetString() == sharedExternalId &&
            supplier.GetProperty("name").GetString() == $"Visible supplier {suffix}");
        suppliers.Should().NotContain(supplier =>
            supplier.GetProperty("name").GetString()!.Contains("Other-tenant", StringComparison.Ordinal));
    }

    public void Dispose() => _factory.Dispose();

    private static Supplier Supplier(string tenantId, string externalId, string name) => new()
    {
        TenantId = tenantId,
        ExternalId = externalId,
        Name = name
    };
}
