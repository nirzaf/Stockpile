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
public sealed class CompanyBranchesAuthorizationPostgreSqlApiTests(PostgreSqlIntegrationFixture fixture) : IDisposable
{
    private readonly PostgreSqlCompanyApiFactory _factory = new(
        fixture, applicationName: "merconiq-company-branches-authorization-api");

    [PostgreSqlFact]
    public async Task View_member_cannot_list_branches_for_an_ungranted_company()
    {
        fixture.EnsureEnabled();
        var suffix = Guid.NewGuid().ToString("N");
        var authorizedBranchCode = $"AUTH-BR-{suffix[..10]}";
        var restrictedBranchCode = $"DENY-BR-{suffix[..10]}";
        int authorizedCompanyId;
        int restrictedCompanyId;

        await using (var seed = fixture.CreateContext("test-tenant"))
        {
            var authorizedCompany = new Company
            {
                Code = $"AUTH-{suffix[..10]}",
                LegalName = "Synthetic authorized company",
                BaseCurrency = "USD"
            };
            var restrictedCompany = new Company
            {
                Code = $"DENY-{suffix[..10]}",
                LegalName = "Synthetic restricted company",
                BaseCurrency = "USD"
            };
            seed.AddRange(
                authorizedCompany,
                restrictedCompany,
                new Branch
                {
                    Company = authorizedCompany,
                    Code = authorizedBranchCode,
                    Name = "Authorized branch"
                },
                new Branch
                {
                    Company = restrictedCompany,
                    Code = restrictedBranchCode,
                    Name = "Restricted branch"
                });
            await seed.SaveChangesAsync();
            authorizedCompanyId = authorizedCompany.Id;
            restrictedCompanyId = restrictedCompany.Id;
        }

        var auditor = await _factory.EnsurePersonaUserAsync("RestrictedAuditor", suffix);
        await using (var grant = fixture.CreateContext("test-tenant"))
        {
            grant.CompanyMemberships.Add(new CompanyMembership
            {
                CompanyId = authorizedCompanyId,
                UserId = auditor.Id,
                Capabilities = CompanyCapability.View,
                IsActive = true
            });
            await grant.SaveChangesAsync();
        }

        using var client = _factory.CreateAuthenticatedClient(auditor, "RestrictedAuditor");
        using var authorizedResponse = await client.GetAsync(
            $"/api/v1/organization/companies/{authorizedCompanyId}/branches");
        authorizedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var body = await authorizedResponse.Content.ReadFromJsonAsync<JsonDocument>())
        {
            body!.RootElement.GetProperty("data").EnumerateArray()
                .Select(branch => branch.GetProperty("code").GetString())
                .Should().Equal(authorizedBranchCode);
        }

        using var restrictedResponse = await client.GetAsync(
            $"/api/v1/organization/companies/{restrictedCompanyId}/branches");
        restrictedResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a company View grant must not authorize listing another company's branches in the same tenant");

        await using var verify = fixture.CreateContext("test-tenant");
        (await verify.Branches.CountAsync(branch =>
            branch.Code == authorizedBranchCode || branch.Code == restrictedBranchCode)).Should().Be(2,
            "both synthetic companies have persisted branches, so the denial exercises authorization rather than missing data");
    }

    [PostgreSqlFact]
    public async Task View_member_can_read_a_granted_branch_but_not_another_company_branch()
    {
        fixture.EnsureEnabled();
        var suffix = Guid.NewGuid().ToString("N");
        var authorizedBranchCode = $"AUTH-DETAIL-{suffix[..10]}";
        var restrictedBranchCode = $"DENY-DETAIL-{suffix[..10]}";
        int authorizedCompanyId;
        int restrictedCompanyId;
        int authorizedBranchId;
        int restrictedBranchId;

        await using (var seed = fixture.CreateContext("test-tenant"))
        {
            var authorizedCompany = new Company
            {
                Code = $"AUTH-DETAIL-{suffix[..10]}",
                LegalName = "Synthetic authorized detail company",
                BaseCurrency = "USD"
            };
            var restrictedCompany = new Company
            {
                Code = $"DENY-DETAIL-{suffix[..10]}",
                LegalName = "Synthetic restricted detail company",
                BaseCurrency = "USD"
            };
            var authorizedBranch = new Branch
            {
                Company = authorizedCompany,
                Code = authorizedBranchCode,
                Name = "Authorized detail branch"
            };
            var restrictedBranch = new Branch
            {
                Company = restrictedCompany,
                Code = restrictedBranchCode,
                Name = "Restricted detail branch"
            };
            seed.AddRange(authorizedCompany, restrictedCompany, authorizedBranch, restrictedBranch);
            await seed.SaveChangesAsync();
            authorizedCompanyId = authorizedCompany.Id;
            restrictedCompanyId = restrictedCompany.Id;
            authorizedBranchId = authorizedBranch.Id;
            restrictedBranchId = restrictedBranch.Id;
        }

        var auditor = await _factory.EnsurePersonaUserAsync("RestrictedAuditor", suffix);
        await using (var grant = fixture.CreateContext("test-tenant"))
        {
            grant.CompanyMemberships.Add(new CompanyMembership
            {
                CompanyId = authorizedCompanyId,
                UserId = auditor.Id,
                Capabilities = CompanyCapability.View,
                IsActive = true
            });
            await grant.SaveChangesAsync();
        }

        using var client = _factory.CreateAuthenticatedClient(auditor, "RestrictedAuditor");
        using var authorizedResponse = await client.GetAsync(
            $"/api/v1/organization/branches/{authorizedBranchId}");
        authorizedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var body = await authorizedResponse.Content.ReadFromJsonAsync<JsonDocument>())
        {
            var branch = body!.RootElement.GetProperty("data");
            branch.GetProperty("id").GetInt32().Should().Be(authorizedBranchId);
            branch.GetProperty("companyId").GetInt32().Should().Be(authorizedCompanyId);
            branch.GetProperty("code").GetString().Should().Be(authorizedBranchCode);
        }

        using var restrictedResponse = await client.GetAsync(
            $"/api/v1/organization/branches/{restrictedBranchId}");
        restrictedResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a View grant for one company must not authorize reading another company's branch in the same tenant");

        await using var verify = fixture.CreateContext("test-tenant");
        (await verify.Branches.CountAsync(branch =>
            branch.Id == authorizedBranchId || branch.Id == restrictedBranchId)).Should().Be(2,
            "both branches are persisted, so the forbidden response proves company-scope authorization");
        restrictedCompanyId.Should().NotBe(authorizedCompanyId);
    }

    public void Dispose() => _factory.Dispose();
}
