using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Core.Services;
using Merconiq.Infrastructure.Data;
using Merconiq.Infrastructure.Repositories;
using Merconiq.Infrastructure.Services;
using Merconiq.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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
            var company = new Company { Code = "CURRENCY", LegalName = "Currency company", BaseCurrency = "QAR", CurrencyScale = 2 };
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
            new UpdateCompanyRequest("Currency company", null, null, null, "USD", null, true, CurrencyScale: 2));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("A company's base currency cannot change after posted stock activity.");
        (await context.Companies.SingleAsync()).BaseCurrency.Should().Be("QAR");
    }

    [PostgreSqlFact]
    public async Task Company_currency_scale_is_frozen_after_posted_stock_activity()
    {
        fixture.EnsureEnabled();
        var tenantId = $"currency-scale-freeze-{Guid.NewGuid():N}";
        int companyId;

        await using (var setup = fixture.CreateContext(tenantId))
        {
            var company = new Company
            {
                Code = "SCALE",
                LegalName = "Scale company",
                BaseCurrency = "USD",
                CurrencyScale = 2
            };
            var branch = new Branch { Company = company, Code = "BRANCH", Name = "Branch" };
            var location = new Location { Branch = branch, Name = "Warehouse" };
            var item = new Item { ItemCode = "SCALE-ITEM", Description = "Scale fixture", Rate = 1m };
            setup.AddRange(company, branch, location, item);
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
            new UpdateCompanyRequest("Scale company", null, null, null, "USD", null, true, CurrencyScale: 3));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("A company's currency scale cannot change after posted stock activity.");
        (await context.Companies.SingleAsync()).CurrencyScale.Should().Be(2);
    }

    [PostgreSqlFact]
    public async Task Company_currency_change_waits_for_an_inflight_stock_posting()
    {
        fixture.EnsureEnabled();
        var tenantId = $"currency-stock-race-{Guid.NewGuid():N}";
        var token = Guid.NewGuid().ToString("N");
        int companyId;
        int itemId;
        int locationId;

        await using (var setup = fixture.CreateContext(tenantId))
        {
            var company = new Company
            {
                Code = $"C-{token[..12]}",
                LegalName = "Currency stock race",
                BaseCurrency = "USD",
                CurrencyScale = 2
            };
            var branch = new Branch { Company = company, Code = "BRANCH", Name = "Branch" };
            var location = new Location { Branch = branch, Name = "Warehouse" };
            var item = new Item { ItemCode = $"ITEM-{token[..12]}", Description = "Race item", Rate = 1m };
            setup.AddRange(company, branch, location, item);
            await setup.SaveChangesAsync();
            companyId = company.Id;
            itemId = item.Id;
            locationId = location.Id;
        }

        await using var blocker = fixture.CreateContext(tenantId, $"currency-stock-blocker-{token}");
        var blockerUnitOfWork = new UnitOfWork(blocker);
        await blockerUnitOfWork.BeginTransactionAsync();
        await blockerUnitOfWork.AcquireTenantOperationLockAsync("organization-state");

        var postingName = $"currency-stock-posting-{token}";
        var posting = RunReceiveStockAsync(fixture, tenantId, postingName, itemId, locationId);
        await using var monitor = fixture.CreateContext(tenantId, $"currency-stock-monitor-{token}");
        await WaitForLockWaitAsync(monitor, postingName, "advisory");

        var updateName = $"currency-stock-update-{token}";
        var update = RunCompanyUpdateAsync(fixture, tenantId, updateName, companyId,
            isActive: true, currencyScale: 3, baseCurrency: "USD");
        await WaitForLockWaitAsync(monitor, updateName, "advisory");

        await blockerUnitOfWork.CommitTransactionAsync();
        await posting;
        var updateAct = () => update;
        await updateAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("A company's currency scale cannot change after posted stock activity.");
    }

    [PostgreSqlFact]
    public async Task Legacy_company_currency_scale_can_be_initialized_after_posted_stock_activity()
    {
        fixture.EnsureEnabled();
        var tenantId = $"currency-scale-initialize-{Guid.NewGuid():N}";
        int companyId;

        await using (var setup = fixture.CreateContext(tenantId))
        {
            var company = new Company
            {
                Code = "SCALE-INIT",
                LegalName = "Scale initialization company",
                BaseCurrency = "USD"
            };
            var branch = new Branch { Company = company, Code = "BRANCH", Name = "Branch" };
            var location = new Location { Branch = branch, Name = "Warehouse" };
            var item = new Item { ItemCode = "SCALE-INIT-ITEM", Description = "Scale fixture", Rate = 1m };
            setup.AddRange(company, branch, location, item);
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
        await CreateService(context, tenantId).UpdateCompanyAsync(companyId,
            new UpdateCompanyRequest("Scale initialization company", null, null, null,
                "USD", null, true, CurrencyScale: 2));

        (await context.Companies.SingleAsync()).CurrencyScale.Should().Be(2);
    }

    [PostgreSqlFact]
    public async Task Location_branch_ownership_cannot_change_after_posted_stock_activity()
    {
        fixture.EnsureEnabled();
        var tenantId = $"location-ownership-{Guid.NewGuid():N}";
        int locationId;
        int originalBranchId;
        int newBranchId;

        await using (var setup = fixture.CreateContext(tenantId))
        {
            var company = new Company { Code = "COMPANY", LegalName = "Company" };
            var originalBranch = new Branch { Company = company, Code = "ORIGINAL", Name = "Original" };
            var newBranch = new Branch { Company = company, Code = "NEW", Name = "New" };
            var location = new Location { Branch = originalBranch, Name = "Warehouse" };
            var item = new Item { ItemCode = "OWNERSHIP-ITEM", Description = "Ownership fixture", Rate = 1m };
            setup.Companies.Add(company);
            setup.Branches.AddRange(originalBranch, newBranch);
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
            locationId = location.Id;
            originalBranchId = originalBranch.Id;
            newBranchId = newBranch.Id;
        }

        await using var context = fixture.CreateContext(tenantId);
        var service = CreateService(context, tenantId);
        var act = () => service.AssignLocationBranchAsync(locationId, newBranchId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("A location's branch ownership cannot change after posted stock activity.");
        (await context.Locations.SingleAsync(location => location.Id == locationId))
            .BranchId.Should().Be(originalBranchId);
    }

    [PostgreSqlFact]
    public async Task Location_reassignment_waits_for_stock_posting_then_rejects_the_ownership_change()
    {
        fixture.EnsureEnabled();
        var tenantId = $"location-ownership-race-{Guid.NewGuid():N}";
        var token = Guid.NewGuid().ToString("N");
        int locationId;
        int originalBranchId;
        int newBranchId;

        await using (var setup = fixture.CreateContext(tenantId))
        {
            var company = new Company { Code = "COMPANY", LegalName = "Company" };
            var originalBranch = new Branch { Company = company, Code = "ORIGINAL", Name = "Original" };
            var newBranch = new Branch { Company = company, Code = "NEW", Name = "New" };
            var location = new Location { Branch = originalBranch, Name = "Warehouse" };
            var item = new Item { ItemCode = "OWNERSHIP-RACE-ITEM", Description = "Ownership race fixture", Rate = 1m };
            setup.Companies.Add(company);
            setup.Branches.AddRange(originalBranch, newBranch);
            setup.Locations.Add(location);
            setup.Items.Add(item);
            await setup.SaveChangesAsync();
            locationId = location.Id;
            originalBranchId = originalBranch.Id;
            newBranchId = newBranch.Id;
        }

        var posterName = $"ownership-poster-{token}";
        await using var poster = fixture.CreateContext(tenantId, posterName);
        var posterUnitOfWork = new UnitOfWork(poster);
        await posterUnitOfWork.BeginTransactionAsync();
        await posterUnitOfWork.AcquireLocationLocksAsync([locationId]);
        poster.StockTransactions.Add(new StockTransaction
        {
            ItemId = await poster.Items.Where(item => item.ItemCode == "OWNERSHIP-RACE-ITEM")
                .Select(item => item.Id).SingleAsync(),
            FromLocationId = locationId,
            Quantity = 1,
            TransactionType = TransactionType.Receive
        });
        await posterUnitOfWork.SaveChangesAsync();

        var assignmentName = $"ownership-assignment-{token}";
        var assignment = RunLocationAssignmentAsync(fixture, tenantId, assignmentName, locationId, newBranchId);
        await using var monitor = fixture.CreateContext(tenantId, $"ownership-monitor-{token}");
        await WaitForLockWaitAsync(monitor, assignmentName, "advisory");

        await posterUnitOfWork.CommitTransactionAsync();
        var result = () => assignment;
        await result.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("A location's branch ownership cannot change after posted stock activity.");

        await using var verify = fixture.CreateContext(tenantId);
        (await verify.Locations.SingleAsync(location => location.Id == locationId))
            .BranchId.Should().Be(originalBranchId);
    }

    [PostgreSqlFact]
    public async Task Stock_posting_rejects_a_company_scope_stale_after_location_reassignment()
    {
        fixture.EnsureEnabled();
        var tenantId = $"location-scope-race-{Guid.NewGuid():N}";
        var token = Guid.NewGuid().ToString("N");
        int itemId;
        int locationId;
        int originalBranchId;
        int newBranchId;
        int originalCompanyId;

        await using (var setup = fixture.CreateContext(tenantId))
        {
            var originalCompany = new Company { Code = $"A-{token[..10]}", LegalName = "Original" };
            var newCompany = new Company { Code = $"B-{token[..10]}", LegalName = "New" };
            var originalBranch = new Branch { Company = originalCompany, Code = "ORIGINAL", Name = "Original" };
            var newBranch = new Branch { Company = newCompany, Code = "NEW", Name = "New" };
            var location = new Location { Branch = originalBranch, Name = "Warehouse" };
            var item = new Item { ItemCode = $"SCOPE-{token[..10]}", Description = "Scope fixture", Rate = 1m };
            setup.Companies.AddRange(originalCompany, newCompany);
            setup.Branches.AddRange(originalBranch, newBranch);
            setup.Locations.Add(location);
            setup.Items.Add(item);
            await setup.SaveChangesAsync();
            itemId = item.Id;
            locationId = location.Id;
            originalBranchId = originalBranch.Id;
            newBranchId = newBranch.Id;
            originalCompanyId = originalCompany.Id;
        }

        await using (var reassignment = fixture.CreateContext(tenantId, $"scope-reassign-{token}"))
        {
            await CreateService(reassignment, tenantId).AssignLocationBranchAsync(locationId, newBranchId);
        }

        await using var context = fixture.CreateContext(tenantId, $"scope-post-{token}");
        var unitOfWork = new UnitOfWork(context);
        var stockService = CreateStockService(context, tenantId, unitOfWork);
        var act = () => stockService.ReceiveStockAsync(
            itemId, locationId, 1, "previously authorized under original company",
            mutationScope: new StockMutationScope(originalCompanyId));

        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("Location ownership changed while stock access was being authorized.*");
        (await context.Locations.SingleAsync(location => location.Id == locationId))
            .BranchId.Should().Be(newBranchId);
        (await context.StockInHand.CountAsync()).Should().Be(0);
        (await context.StockTransactions.CountAsync()).Should().Be(0);
        originalBranchId.Should().NotBe(newBranchId);
    }

    private static async Task RunCompanyUpdateAsync(
        PostgreSqlIntegrationFixture fixture,
        string tenantId,
        string applicationName,
        int companyId,
        bool isActive,
        int currencyScale = 2,
        string baseCurrency = "QAR")
    {
        await using var context = fixture.CreateContext(tenantId, applicationName);
        var service = CreateService(context, tenantId);
        await service.UpdateCompanyAsync(companyId,
            new UpdateCompanyRequest("Concurrent company", null, null, null, baseCurrency, null, isActive,
                CurrencyScale: currencyScale));
    }

    private static async Task RunReceiveStockAsync(
        PostgreSqlIntegrationFixture fixture,
        string tenantId,
        string applicationName,
        int itemId,
        int locationId)
    {
        await using var context = fixture.CreateContext(tenantId, applicationName);
        var unitOfWork = new UnitOfWork(context);
        await CreateStockService(context, tenantId, unitOfWork)
            .ReceiveStockAsync(itemId, locationId, 1, "Concurrent stock posting");
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

    private static async Task RunLocationAssignmentAsync(
        PostgreSqlIntegrationFixture fixture,
        string tenantId,
        string applicationName,
        int locationId,
        int branchId)
    {
        await using var context = fixture.CreateContext(tenantId, applicationName);
        var service = CreateService(context, tenantId);
        await service.AssignLocationBranchAsync(locationId, branchId);
    }

    private static OrganizationService CreateService(InventoryDbContext context, string tenantId) => new(
        new Repository<Company>(context),
        new Repository<Branch>(context),
        new Repository<Location>(context),
        new UnitOfWork(context),
        new TestTenantContext(tenantId),
        context);

    private static StockService CreateStockService(
        InventoryDbContext context,
        string tenantId,
        IUnitOfWork unitOfWork) => new(
        new Repository<StockInHand>(context),
        new Repository<StockTransaction>(context),
        new Repository<Item>(context),
        new Repository<Location>(context),
        new Repository<Branch>(context),
        unitOfWork,
        new Mock<IWebhookDispatcher>().Object,
        new TestTenantContext(tenantId),
        NullLogger<StockService>.Instance,
        new Repository<StockValuationBucket>(context),
        new Repository<StockValuationEntry>(context),
        new Repository<StockReservation>(context),
        new Repository<StockReservationAllocation>(context));

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
