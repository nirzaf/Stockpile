using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class MasterDataImportAuthorizationPostgreSqlApiTests(
    PostgreSqlIntegrationFixture fixture) : IDisposable
{
    private readonly PostgreSqlCompanyApiFactory _factory = new(
        fixture, applicationName: "merconiq-master-data-import-authorization-api");

    [PostgreSqlFact]
    public async Task Import_endpoints_enforce_tenant_and_company_scopes_without_cross_company_writes()
    {
        fixture.EnsureEnabled();
        var suffix = Guid.NewGuid().ToString("N");
        int companyAId;
        int companyBId;

        await using (var seed = fixture.CreateContext("test-tenant"))
        {
            var companyA = new Company
            {
                Code = $"IMPA-{suffix[..8]}",
                LegalName = "Synthetic authorized import company",
                BaseCurrency = "USD",
                CurrencyScale = 2
            };
            var companyB = new Company
            {
                Code = $"IMPB-{suffix[..8]}",
                LegalName = "Synthetic restricted import company",
                BaseCurrency = "USD",
                CurrencyScale = 2
            };
            seed.Companies.AddRange(companyA, companyB);
            await seed.SaveChangesAsync();
            companyAId = companyA.Id;
            companyBId = companyB.Id;
        }

        var adminUser = await _factory.EnsurePersonaUserAsync("Admin", suffix);
        var buyerUser = await _factory.EnsurePersonaUserAsync("Buyer", suffix);
        await using (var membership = fixture.CreateContext("test-tenant"))
        {
            membership.CompanyMemberships.Add(new CompanyMembership
            {
                CompanyId = companyAId,
                UserId = buyerUser.Id,
                Capabilities = CompanyCapability.View | CompanyCapability.Edit,
                IsActive = true
            });
            await membership.SaveChangesAsync();
        }

        using var adminClient = _factory.CreateAuthenticatedClient(adminUser, "Admin");
        using var buyerClient = _factory.CreateAuthenticatedClient(buyerUser, "Buyer");

        var forbiddenCompanyExternalId = $"company-denied-{suffix}";
        var companyCsv = "external_id,code,legal_name,trading_name,registration_number,tax_identifier,base_currency,country_code,currency_scale,is_active\n"
            + $"{forbiddenCompanyExternalId},DENY-{suffix[..8]},Denied Company,,,TAX,USD,US,2,true";
        using (var deniedCompanyImport = await buyerClient.PostAsJsonAsync(
                   "/api/v1/organization/companies/import",
                   new { csv = companyCsv, dryRun = false }))
        {
            deniedCompanyImport.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "a company membership must not grant tenant-wide company creation");
        }

        var companyExternalId = $"company-admin-{suffix}";
        var authorizedCompanyCsv = "external_id,code,legal_name,trading_name,registration_number,tax_identifier,base_currency,country_code,currency_scale,is_active\n"
            + $"{companyExternalId},NEW-{suffix[..8]},Synthetic New Company,,,TAX,USD,US,2,true";
        using (var allowedCompanyImport = await adminClient.PostAsJsonAsync(
                   "/api/v1/organization/companies/import",
                   new { csv = authorizedCompanyCsv, dryRun = false }))
        {
            allowedCompanyImport.StatusCode.Should().Be(HttpStatusCode.OK);
            using var body = await allowedCompanyImport.Content.ReadFromJsonAsync<JsonDocument>();
            body!.RootElement.GetProperty("data").GetProperty("created").GetInt32().Should().Be(1);
        }

        var unitExternalId = $"unit-{suffix[..10]}";
        var unitCode = $"EA{suffix[..8]}";
        var unitCsv = "external_id,code,name,decimal_places,whole_unit_only\n"
            + $"{unitExternalId},{unitCode},Synthetic Each,0,true";
        using (var deniedUnitImport = await buyerClient.PostAsJsonAsync(
                   "/api/v1/organization/units/import",
                   new { csv = unitCsv, dryRun = false, companyId = companyAId }))
        {
            deniedUnitImport.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "unit imports require both an active company scope and tenant-admin authority");
        }
        using (var allowedUnitImport = await adminClient.PostAsJsonAsync(
                   "/api/v1/organization/units/import",
                   new { csv = unitCsv, dryRun = false, companyId = companyAId }))
        {
            allowedUnitImport.StatusCode.Should().Be(HttpStatusCode.OK);
            using var body = await allowedUnitImport.Content.ReadFromJsonAsync<JsonDocument>();
            body!.RootElement.GetProperty("data").GetProperty("created").GetInt32().Should().Be(1);
        }

        var authorizedItemExternalId = $"item-{suffix[..10]}";
        var itemCode = $"SKU-{suffix[..8]}";
        // The imported unit is whole-unit-only, so the item must not allow fractional quantities.
        var authorizedItemCsv = "external_id,item_code,description,rate,base_unit_external_id,purchase_unit_external_id,sales_unit_external_id,purchase_to_base_factor,sales_to_base_factor,quantity_precision,whole_unit_only\n"
            + $"{authorizedItemExternalId},{itemCode},Synthetic Item,12.50,{unitExternalId},,,1,1,0,true";
        using (var allowedItemImport = await buyerClient.PostAsJsonAsync(
                   "/api/v1/organization/items/import",
                   new { csv = authorizedItemCsv, dryRun = false, companyId = companyAId }))
        {
            allowedItemImport.StatusCode.Should().Be(HttpStatusCode.OK);
            using var body = await allowedItemImport.Content.ReadFromJsonAsync<JsonDocument>();
            body!.RootElement.GetProperty("data").GetProperty("created").GetInt32().Should().Be(1);
        }

        var deniedItemExternalId = $"item-denied-{suffix[..8]}";
        var deniedItemCsv = "external_id,item_code,description,rate,base_unit_external_id,purchase_unit_external_id,sales_unit_external_id,purchase_to_base_factor,sales_to_base_factor,quantity_precision,whole_unit_only\n"
            + $"{deniedItemExternalId},DENY-{suffix[..8]},Denied Item,1.00,{unitExternalId},,,1,1,0,true";
        using (var deniedItemImport = await buyerClient.PostAsJsonAsync(
                   "/api/v1/organization/items/import",
                   new { csv = deniedItemCsv, dryRun = false, companyId = companyBId }))
        {
            deniedItemImport.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        var supplierExternalId = $"supplier-{suffix[..10]}";
        var supplierCsv = "external_id,name,contact_person,phone,email,address\n"
            + $"{supplierExternalId},Synthetic Supplier,Buyer,+10000000000,buyer@example.test,Test City";
        using (var missingSupplierScope = await buyerClient.PostAsJsonAsync(
                   "/api/v1/organization/suppliers/import",
                   new { csv = supplierCsv, dryRun = false }))
        {
            missingSupplierScope.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                "shared supplier imports must name the company permission scope explicitly");
        }
        using (var allowedSupplierImport = await buyerClient.PostAsJsonAsync(
                   "/api/v1/organization/suppliers/import",
                   new { csv = supplierCsv, dryRun = false, companyId = companyAId }))
        {
            allowedSupplierImport.StatusCode.Should().Be(HttpStatusCode.OK);
            using var body = await allowedSupplierImport.Content.ReadFromJsonAsync<JsonDocument>();
            body!.RootElement.GetProperty("data").GetProperty("created").GetInt32().Should().Be(1);
        }

        var deniedSupplierExternalId = $"supplier-denied-{suffix[..8]}";
        var deniedSupplierCsv = "external_id,name,contact_person,phone,email,address\n"
            + $"{deniedSupplierExternalId},Denied Supplier,,,,";
        using (var deniedSupplierImport = await buyerClient.PostAsJsonAsync(
                   "/api/v1/organization/suppliers/import",
                   new { csv = deniedSupplierCsv, dryRun = false, companyId = companyBId }))
        {
            deniedSupplierImport.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        await using var verify = fixture.CreateContext("test-tenant");
        (await verify.Companies.IgnoreQueryFilters().CountAsync(company =>
            company.TenantId == "test-tenant" &&
            company.ExternalId == forbiddenCompanyExternalId)).Should().Be(0);
        (await verify.Companies.IgnoreQueryFilters().CountAsync(company =>
            company.TenantId == "test-tenant" &&
            company.ExternalId == companyExternalId)).Should().Be(1);
        (await verify.UnitsOfMeasure.IgnoreQueryFilters().CountAsync(unit =>
            unit.TenantId == "test-tenant" &&
            unit.ExternalId == unitExternalId)).Should().Be(1);
        (await verify.Items.IgnoreQueryFilters().CountAsync(item =>
            item.TenantId == "test-tenant" &&
            item.ExternalId == authorizedItemExternalId)).Should().Be(1);
        (await verify.Items.IgnoreQueryFilters().CountAsync(item =>
            item.TenantId == "test-tenant" &&
            item.ExternalId == deniedItemExternalId)).Should().Be(0);
        (await verify.Suppliers.IgnoreQueryFilters().CountAsync(supplier =>
            supplier.TenantId == "test-tenant" &&
            supplier.ExternalId == supplierExternalId)).Should().Be(1);
        (await verify.Suppliers.IgnoreQueryFilters().CountAsync(supplier =>
            supplier.TenantId == "test-tenant" &&
            supplier.ExternalId == deniedSupplierExternalId)).Should().Be(0);
    }

    public void Dispose() => _factory.Dispose();
}
