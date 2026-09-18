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

public sealed class ChartOfAccountsApiTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    [Fact]
    public async Task Chart_accounts_are_company_scoped_audited_and_read_only_for_auditors()
    {
        using var buyer = factory.CreateAuthenticatedClient("Buyer");
        using var auditor = factory.CreateAuthenticatedClient("RestrictedAuditor");
        var companies = await SeedCompanyScopeAsync(factory, Guid.NewGuid().ToString("N")[..10]);
        var route = $"/api/v1/companies/{companies.CompanyAId}/chart-of-accounts";

        (await buyer.GetAsync($"{route}?page=1&pageSize=100")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await buyer.GetAsync($"{route}?page=1&pageSize=101")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await buyer.GetAsync(
                $"/api/v1/companies/{companies.CompanyBId}/chart-of-accounts"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var groupResponse = await buyer.PostAsJsonAsync(route, new
        {
            accountCode = " SYN-GROUP-01 ",
            name = " Synthetic grouping label ",
            accountType = " Example classification ",
            isGroupAccount = true,
            companyId = companies.CompanyBId,
            tenantId = "forged-tenant"
        });
        groupResponse.StatusCode.Should().Be(HttpStatusCode.Created,
            await groupResponse.Content.ReadAsStringAsync());
        using var groupBody = await groupResponse.Content.ReadFromJsonAsync<JsonDocument>();
        var group = groupBody!.RootElement.GetProperty("data");
        var groupId = group.GetProperty("id").GetInt32();
        group.GetProperty("companyId").GetInt32().Should().Be(companies.CompanyAId);
        group.GetProperty("accountCode").GetString().Should().Be("SYN-GROUP-01");
        group.GetProperty("accountType").GetString().Should().Be("Example classification");
        group.GetProperty("isGroupAccount").GetBoolean().Should().BeTrue();

        var child = await buyer.PostAsJsonAsync(route, new
        {
            accountCode = "SYN-LEAF-01",
            name = "Synthetic leaf label",
            accountType = "Example classification",
            parentAccountId = groupId
        });
        child.StatusCode.Should().Be(HttpStatusCode.Created,
            await child.Content.ReadAsStringAsync());
        using var childBody = await child.Content.ReadFromJsonAsync<JsonDocument>();
        var childId = childBody!.RootElement.GetProperty("data").GetProperty("id").GetInt32();

        var inactiveGroup = await buyer.PostAsJsonAsync(route, new
        {
            accountCode = "SYN-INACTIVE-GROUP",
            name = "Synthetic inactive group",
            accountType = "Example classification",
            isGroupAccount = true,
            isActive = false
        });
        inactiveGroup.StatusCode.Should().Be(HttpStatusCode.Created);
        using var inactiveGroupBody = await inactiveGroup.Content.ReadFromJsonAsync<JsonDocument>();
        var inactiveGroupId = inactiveGroupBody!.RootElement.GetProperty("data").GetProperty("id").GetInt32();
        var inactiveParent = await buyer.PostAsJsonAsync(route, new
        {
            accountCode = "SYN-CHILD-OF-INACTIVE",
            name = "Synthetic child of inactive group",
            accountType = "Example classification",
            parentAccountId = inactiveGroupId
        });
        inactiveParent.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var duplicate = await buyer.PostAsJsonAsync(route, new
        {
            accountCode = "SYN-GROUP-01",
            name = "Duplicate synthetic label",
            accountType = "Example classification",
            isGroupAccount = true
        });
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var foreignParent = await buyer.PostAsJsonAsync(route, new
        {
            accountCode = "SYN-FOREIGN-PARENT",
            name = "Synthetic invalid parent",
            accountType = "Example classification",
            parentAccountId = companies.CompanyBGroupId
        });
        foreignParent.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var nonGroupParent = await buyer.PostAsJsonAsync(route, new
        {
            accountCode = "SYN-INVALID-CHILD",
            name = "Synthetic child of leaf",
            accountType = "Example classification",
            parentAccountId = childId
        });
        nonGroupParent.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using (var auditorList = await auditor.GetAsync(route))
        {
            auditorList.StatusCode.Should().Be(HttpStatusCode.OK);
            using var body = await auditorList.Content.ReadFromJsonAsync<JsonDocument>();
            body!.RootElement.GetProperty("data").GetProperty("items").GetArrayLength().Should().Be(2);
        }
        using (var auditorListIncludingInactive = await auditor.GetAsync($"{route}?includeInactive=true"))
        {
            using var body = await auditorListIncludingInactive.Content.ReadFromJsonAsync<JsonDocument>();
            body!.RootElement.GetProperty("data").GetProperty("items").GetArrayLength().Should().Be(3);
        }

        (await auditor.PostAsJsonAsync(route, new
        {
            accountCode = "SYN-AUDITOR-WRITE",
            name = "Synthetic forbidden write",
            accountType = "Example classification"
        })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var persisted = await db.ChartOfAccounts.SingleAsync(account => account.Id == groupId);
        persisted.CompanyId.Should().Be(companies.CompanyAId);
        persisted.TenantId.Should().Be("test-tenant");
        (await db.AuditLogs.AnyAsync(audit =>
                audit.EntityName == nameof(ChartOfAccount) &&
                audit.Action == "Insert" &&
                audit.TenantId == "test-tenant"))
            .Should().BeTrue();
    }

    private static async Task<TestCompanyScope> SeedCompanyScopeAsync(
        CustomWebApplicationFactory factory,
        string suffix)
    {
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<InventoryDbContext>();
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var buyer = (await userManager.FindByNameAsync("buyer@test-tenant.test"))!;
        var auditor = (await userManager.FindByNameAsync("restrictedauditor@test-tenant.test"))!;

        var companyA = new Company
        {
            Code = $"CHART-A-{suffix}",
            LegalName = "Synthetic chart company A",
            BaseCurrency = "TST"
        };
        var companyB = new Company
        {
            Code = $"CHART-B-{suffix}",
            LegalName = "Synthetic chart company B",
            BaseCurrency = "TST"
        };
        db.AddRange(companyA, companyB);
        await db.SaveChangesAsync();
        var foreignGroup = new ChartOfAccount
        {
            CompanyId = companyB.Id,
            AccountCode = "SYN-FOREIGN-GROUP",
            Name = "Synthetic foreign group",
            AccountType = "Example classification",
            IsGroupAccount = true
        };
        db.ChartOfAccounts.Add(foreignGroup);
        await db.SaveChangesAsync();

        db.CompanyMemberships.AddRange(
            new CompanyMembership
            {
                CompanyId = companyA.Id,
                UserId = buyer.Id,
                Capabilities = CompanyCapability.View | CompanyCapability.Edit,
                IsActive = true
            },
            new CompanyMembership
            {
                CompanyId = companyA.Id,
                UserId = auditor.Id,
                Capabilities = CompanyCapability.View,
                IsActive = true
            });
        await db.SaveChangesAsync();

        return new TestCompanyScope(companyA.Id, companyB.Id, foreignGroup.Id);
    }

    private sealed record TestCompanyScope(int CompanyAId, int CompanyBId, int CompanyBGroupId);
}
