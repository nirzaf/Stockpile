using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Merconiq.Infrastructure.Repositories;
using Merconiq.Infrastructure.Services;
using Merconiq.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class OrganizationPostgreSqlIntegrationTests(PostgreSqlIntegrationFixture fixture)
{
    [PostgreSqlFact]
    public async Task Company_deactivation_and_branch_activation_are_serialized()
    {
        fixture.EnsureEnabled();
        var tenantId = $"organization-race-{Guid.NewGuid():N}";
        var token = Guid.NewGuid().ToString("N");
        int companyId;
        int branchId;

        await using (var setup = fixture.CreateContext(tenantId))
        {
            var company = new Company { Code = $"C-{token[..12]}", LegalName = "Concurrent company" };
            setup.Companies.Add(company);
            await setup.SaveChangesAsync();
            var branch = new Branch
            {
                CompanyId = company.Id,
                Code = $"B-{token[..12]}",
                Name = "Inactive branch",
                IsActive = false
            };
            setup.Branches.Add(branch);
            await setup.SaveChangesAsync();
            companyId = company.Id;
            branchId = branch.Id;
        }

        var deactivateApp = $"organization-deactivate-{token}";
        var activateApp = $"organization-activate-{token}";
        await using var blocker = fixture.CreateContext(tenantId, $"organization-blocker-{token}");
        await blocker.Database.BeginTransactionAsync();
        await blocker.Companies
            .FromSqlInterpolated($"SELECT * FROM \"Companies\" WHERE \"Id\" = {companyId} FOR UPDATE")
            .SingleAsync();

        var deactivate = RunCompanyUpdateAsync(fixture, tenantId, deactivateApp, companyId, isActive: false);
        await using var monitor = fixture.CreateContext(tenantId, $"organization-monitor-{token}");
        await WaitForLockWaitAsync(monitor, deactivateApp, "transactionid");

        var activate = RunBranchUpdateAsync(fixture, tenantId, activateApp, branchId, isActive: true);
        await WaitForLockWaitAsync(monitor, activateApp, "advisory");

        await blocker.Database.CommitTransactionAsync();
        await deactivate;
        var activation = () => activate;
        await activation.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The company does not exist in this tenant or is inactive.");

        await using var verify = fixture.CreateContext(tenantId);
        (await verify.Companies.SingleAsync(company => company.Id == companyId)).IsActive.Should().BeFalse();
        (await verify.Branches.SingleAsync(branch => branch.Id == branchId)).IsActive.Should().BeFalse();
    }

    [PostgreSqlFact]
    public async Task Company_base_currency_cannot_change_after_stock_activity()
    {
        fixture.EnsureEnabled();
        var tenantId = $"currency-freeze-{Guid.NewGuid():N}";
        int companyId;

        await using (var setup = fixture.CreateContext(tenantId))
        {
            var company = new Company { Code = "CURRENCY", LegalName = "Currency company", BaseCurrency = "QAR" };
            var branch = new Branch { Company = company, Code = "BRANCH", Name = "Branch" };
            var location = new Location { Branch = branch, Name = "Warehouse" };
            var item = new Item { ItemCode = "CURRENCY-ITEM", Description = "Currency fixture", Rate = 1m };
            setup.Companies.Add(company);
            setup.Branches.Add(branch);
            setup.Locations.Add(location);
            setup.Items.Add(item);
            await setup.SaveChangesAsync();
            setup.StockTransactions.Add(new StockTransaction
            {
                ItemId = item.Id,
                FromLocationId = location.Id,
                Quantity = 1,
                TransactionType = TransactionType.Receive
            });
            await setup.SaveChangesAsync();
            companyId = company.Id;
        }

        await using var context = fixture.CreateContext(tenantId);
        var service = CreateService(context, tenantId);
        var act = () => service.UpdateCompanyAsync(companyId,
            new UpdateCompanyRequest("Currency company", null, null, null, "USD", null, true));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("A company's base currency cannot change after posted stock activity.");
        (await context.Companies.SingleAsync()).BaseCurrency.Should().Be("QAR");
    }

    private static async Task RunCompanyUpdateAsync(
        PostgreSqlIntegrationFixture fixture,
        string tenantId,
        string applicationName,
        int companyId,
        bool isActive)
    {
        await using var context = fixture.CreateContext(tenantId, applicationName);
        var service = CreateService(context, tenantId);
        await service.UpdateCompanyAsync(companyId,
            new UpdateCompanyRequest("Concurrent company", null, null, null, "QAR", null, isActive));
    }

    private static async Task RunBranchUpdateAsync(
        PostgreSqlIntegrationFixture fixture,
        string tenantId,
        string applicationName,
        int branchId,
        bool isActive)
    {
        await using var context = fixture.CreateContext(tenantId, applicationName);
        var service = CreateService(context, tenantId);
        await service.UpdateBranchAsync(branchId,
            new UpdateBranchRequest("Inactive branch", null, "UTC", isActive));
    }

    private static OrganizationService CreateService(InventoryDbContext context, string tenantId) => new(
        new Repository<Company>(context),
        new Repository<Branch>(context),
        new Repository<Location>(context),
        new UnitOfWork(context),
        new TestTenantContext(tenantId),
        context);

    private static async Task WaitForLockWaitAsync(
        InventoryDbContext context,
        string applicationName,
        string waitEvent)
    {
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM pg_stat_activity
                WHERE application_name = @application_name
                  AND wait_event_type = 'Lock'
                  AND wait_event = @wait_event)
            """;
        command.Parameters.Add(new NpgsqlParameter("application_name", applicationName));
        command.Parameters.Add(new NpgsqlParameter("wait_event", waitEvent));

        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (Equals(await command.ExecuteScalarAsync(), true))
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException(
            $"The PostgreSQL session '{applicationName}' did not enter the expected '{waitEvent}' lock wait.");
    }
}
