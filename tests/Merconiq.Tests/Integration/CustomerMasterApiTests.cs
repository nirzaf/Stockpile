using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Merconiq.Tests.Integration;

public sealed class CustomerMasterApiTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    [Fact]
    public async Task Customer_master_is_company_scoped_validated_audited_and_deactivatable()
    {
        using var client = factory.CreateAuthenticatedClient("Buyer");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var companies = await SeedCompanyScopeAsync(factory, suffix);

        var invalid = await client.PostAsJsonAsync(
            $"/api/v1/companies/{companies.CompanyAId}/customers",
            new
            {
                customerCode = "invalid-terms",
                name = "Invalid customer",
                contactEmail = "not-an-email",
                paymentTermDays = 3651
            });
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var created = await client.PostAsJsonAsync(
            $"/api/v1/companies/{companies.CompanyAId}/customers",
            new
            {
                customerCode = " acme-01 ",
                name = "  Acme Supply  ",
                contactEmail = " accounts@acme.example ",
                contactPhone = " +1 555 0100 ",
                billingAddress = "  1 Main Street  ",
                shippingAddress = "  2 Warehouse Road  ",
                paymentTermDays = 30
            });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        using var createdBody = await created.Content.ReadFromJsonAsync<JsonDocument>();
        var customerId = createdBody!.RootElement.GetProperty("data").GetProperty("id").GetInt32();
        createdBody.RootElement.GetProperty("data").GetProperty("customerCode").GetString()
            .Should().Be("ACME-01");
        createdBody.RootElement.GetProperty("data").GetProperty("name").GetString()
            .Should().Be("Acme Supply");
        createdBody.RootElement.GetProperty("data").GetProperty("contactEmail").GetString()
            .Should().Be("accounts@acme.example");

        var duplicate = await client.PostAsJsonAsync(
            $"/api/v1/companies/{companies.CompanyAId}/customers",
            new { customerCode = "acme-01", name = "Duplicate code" });
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await client.GetAsync($"/api/v1/companies/{companies.CompanyBId}/customers"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync(
                $"/api/v1/companies/{companies.CompanyAId}/customers/{companies.CompanyBCustomerId}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound,
                "a customer owned by another company is indistinguishable from a missing record");

        using (var list = await client.GetAsync($"/api/v1/companies/{companies.CompanyAId}/customers"))
        {
            list.StatusCode.Should().Be(HttpStatusCode.OK);
            using var listBody = await list.Content.ReadFromJsonAsync<JsonDocument>();
            var items = listBody!.RootElement.GetProperty("data").GetProperty("items");
            items.GetArrayLength().Should().Be(1);
            items[0].GetProperty("id").GetInt32().Should().Be(customerId);
        }

        (await client.PostAsync(
                $"/api/v1/companies/{companies.CompanyAId}/customers/{customerId}/deactivate", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        using (var activeList = await client.GetAsync($"/api/v1/companies/{companies.CompanyAId}/customers"))
        {
            using var activeBody = await activeList.Content.ReadFromJsonAsync<JsonDocument>();
            activeBody!.RootElement.GetProperty("data").GetProperty("items").GetArrayLength().Should().Be(0);
        }

        using (var inactive = await client.GetAsync(
                   $"/api/v1/companies/{companies.CompanyAId}/customers/{customerId}"))
        {
            using var inactiveBody = await inactive.Content.ReadFromJsonAsync<JsonDocument>();
            inactiveBody!.RootElement.GetProperty("data").GetProperty("isActive").GetBoolean().Should().BeFalse();
        }

        (await client.PostAsync(
                $"/api/v1/companies/{companies.CompanyAId}/customers/{customerId}/reactivate", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.PutAsJsonAsync(
                $"/api/v1/companies/{companies.CompanyAId}/customers/{customerId}",
                new
                {
                    customerCode = "acme-02",
                    name = "Acme Supply Ltd",
                    contactEmail = "ar@acme.example",
                    contactPhone = (string?)null,
                    billingAddress = "3 Main Street",
                    shippingAddress = "2 Warehouse Road",
                    paymentTermDays = 45
                }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        using (var detail = await client.GetAsync(
                   $"/api/v1/companies/{companies.CompanyAId}/customers/{customerId}"))
        {
            using var detailBody = await detail.Content.ReadFromJsonAsync<JsonDocument>();
            var data = detailBody!.RootElement.GetProperty("data");
            data.GetProperty("customerCode").GetString().Should().Be("ACME-02");
            data.GetProperty("paymentTermDays").GetInt32().Should().Be(45);
            data.GetProperty("isActive").GetBoolean().Should().BeTrue();
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var auditActions = await db.AuditLogs
            .Where(audit => audit.EntityName == nameof(Customer))
            .Select(audit => audit.Action)
            .ToListAsync();
        auditActions.Should().Contain("Insert").And.Contain("Update");
        (await db.Customers.SingleAsync(customer => customer.Id == customerId)).CompanyId
            .Should().Be(companies.CompanyAId);
    }

    private static async Task<TestCompanyScope> SeedCompanyScopeAsync(
        CustomWebApplicationFactory factory,
        string suffix)
    {
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<InventoryDbContext>();
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var buyer = await userManager.FindByNameAsync("buyer@test-tenant.test");
        buyer.Should().NotBeNull("the authenticated test client creates this user");

        var companyA = new Company
        {
            Code = $"CUST-A-{suffix}",
            LegalName = "Synthetic authorized company",
            BaseCurrency = "USD"
        };
        var companyB = new Company
        {
            Code = $"CUST-B-{suffix}",
            LegalName = "Synthetic restricted company",
            BaseCurrency = "USD"
        };
        var companyBCustomer = new Customer
        {
            Company = companyB,
            CustomerCode = "ACME-01",
            Name = "Company B customer"
        };
        db.AddRange(companyA, companyB, companyBCustomer);
        await db.SaveChangesAsync();

        db.CompanyMemberships.Add(new CompanyMembership
        {
            CompanyId = companyA.Id,
            UserId = buyer!.Id,
            Capabilities = CompanyCapability.View | CompanyCapability.Edit,
            IsActive = true
        });
        await db.SaveChangesAsync();

        return new TestCompanyScope(companyA.Id, companyB.Id, companyBCustomer.Id);
    }

    private sealed record TestCompanyScope(int CompanyAId, int CompanyBId, int CompanyBCustomerId);
}
