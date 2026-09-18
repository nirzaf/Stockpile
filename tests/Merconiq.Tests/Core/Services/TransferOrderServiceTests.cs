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
using System.Linq.Expressions;

namespace Merconiq.Tests.Core.Services;

public sealed class TransferOrderServiceTests
{
    [Fact]
    public async Task Create_persists_a_numbered_order_and_stable_line_identity()
    {
        var tenant = new TestTenantContext("transfer-order-create-test");
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase($"transfer-order-{Guid.NewGuid():N}")
            .Options;
        await using var context = new InventoryDbContext(options, tenant);
        var company = new Company { Code = "TO-CO", LegalName = "Transfer Order Company" };
        context.Companies.Add(company);
        await context.SaveChangesAsync();
        var branchA = new Branch { CompanyId = company.Id, Code = "A", Name = "A" };
        var branchB = new Branch { CompanyId = company.Id, Code = "B", Name = "B" };
        context.Branches.AddRange(branchA, branchB);
        await context.SaveChangesAsync();
        var source = new Location { BranchId = branchA.Id, Name = "Source" };
        var destination = new Location { BranchId = branchB.Id, Name = "Destination" };
        var item = new Item { ItemCode = "TO-ITEM", Description = "Transfer item" };
        context.Locations.AddRange(source, destination);
        context.Items.Add(item);
        await context.SaveChangesAsync();

        var unitOfWork = new UnitOfWork(context);
        var stockService = new Mock<IStockService>();
        var service = new TransferOrderService(
            new Repository<TransferOrder>(context),
            new Repository<TransferOrderLine>(context),
            new Repository<TransferTransitEntry>(context),
            new Repository<DocumentIdentity>(context),
            new Repository<DocumentLineIdentity>(context),
            new Repository<Company>(context),
            new Repository<Branch>(context),
            new Repository<Location>(context),
            new Repository<Item>(context),
            unitOfWork,
            new DocumentIdentityService(context, unitOfWork, new DocumentNumberService(context, unitOfWork)),
            stockService.Object,
            tenant,
            new Mock<IWebhookDispatcher>().Object,
            NullLogger<TransferOrderService>.Instance);

        var result = await service.CreateAsync(new CreateTransferOrderRequest(
            company.Id,
            source.Id,
            destination.Id,
            [new TransferOrderLineRequest(item.Id, 4)]), "create-1");

        result.Number.Should().Be($"TO-{DateTime.UtcNow.Year:0000}-000001");
        result.Status.Should().Be(TransferOrderStatus.Draft);
        result.Lines.Should().ContainSingle().Which.Quantity.Should().Be(4);
        (await context.TransferOrders.CountAsync()).Should().Be(1);
        (await context.DocumentLineIdentities.CountAsync()).Should().Be(1);

        var otherCompany = new Company { Code = "TO-CO-2", LegalName = "Other Transfer Order Company" };
        context.Companies.Add(otherCompany);
        await context.SaveChangesAsync();
        var otherBranchA = new Branch { CompanyId = otherCompany.Id, Code = "A", Name = "A" };
        var otherBranchB = new Branch { CompanyId = otherCompany.Id, Code = "B", Name = "B" };
        context.Branches.AddRange(otherBranchA, otherBranchB);
        await context.SaveChangesAsync();
        var otherSource = new Location { BranchId = otherBranchA.Id, Name = "Other Source" };
        var otherDestination = new Location { BranchId = otherBranchB.Id, Name = "Other Destination" };
        context.Locations.AddRange(otherSource, otherDestination);
        await context.SaveChangesAsync();
        var otherResult = await service.CreateAsync(new CreateTransferOrderRequest(
            otherCompany.Id,
            otherSource.Id,
            otherDestination.Id,
            [new TransferOrderLineRequest(item.Id, 2)]), "create-2");

        (await service.GetRecentForCompaniesAsync([company.Id])).Should()
            .ContainSingle().Which.Id.Should().Be(result.Id);
        (await service.GetRecentForCompaniesAsync([otherCompany.Id])).Should()
            .ContainSingle().Which.Id.Should().Be(otherResult.Id);

        var persistedOrder = await context.TransferOrders.SingleAsync(order => order.Id == result.Id);
        persistedOrder.Status = TransferOrderStatus.Approved;
        await context.SaveChangesAsync();
        await service.AmendAsync(result.Id, new CreateTransferOrderRequest(
            company.Id,
            source.Id,
            destination.Id,
            [new TransferOrderLineRequest(item.Id, 4, LineId: result.Lines[0].Id)]),
            new StockMutationScope(company.Id));

        (await context.TransferOrders.SingleAsync(order => order.Id == result.Id)).Status.Should()
            .Be(TransferOrderStatus.Approved);
        stockService.Verify(stock => stock.ReleaseReservationAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<StockMutationScope?>()), Times.Never);
    }

    [Fact]
    public async Task Amend_adds_and_removes_lines_and_invalidates_approved_order()
    {
        var tenant = new TestTenantContext("transfer-order-amend-lines-test");
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase($"transfer-order-amend-{Guid.NewGuid():N}")
            .Options;
        await using var context = new InventoryDbContext(options, tenant);

        var company = new Company { Code = "TO-AMEND", LegalName = "Transfer Amendment Company" };
        context.Companies.Add(company);
        await context.SaveChangesAsync();
        var sourceBranch = new Branch { CompanyId = company.Id, Code = "A", Name = "A" };
        var destinationBranch = new Branch { CompanyId = company.Id, Code = "B", Name = "B" };
        context.Branches.AddRange(sourceBranch, destinationBranch);
        await context.SaveChangesAsync();
        var source = new Location { BranchId = sourceBranch.Id, Name = "Source" };
        var destination = new Location { BranchId = destinationBranch.Id, Name = "Destination" };
        var item = new Item { ItemCode = "TO-AMEND-ITEM", Description = "Transfer amendment item" };
        context.Locations.AddRange(source, destination);
        context.Items.Add(item);
        await context.SaveChangesAsync();

        var unitOfWork = new UnitOfWork(context);
        var stockService = new Mock<IStockService>();
        stockService.Setup(stock => stock.ReleaseReservationAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<StockMutationScope?>()))
            .Returns(Task.CompletedTask);
        var service = new TransferOrderService(
            new Repository<TransferOrder>(context),
            new Repository<TransferOrderLine>(context),
            new Repository<TransferTransitEntry>(context),
            new Repository<DocumentIdentity>(context),
            new Repository<DocumentLineIdentity>(context),
            new Repository<Company>(context),
            new Repository<Branch>(context),
            new Repository<Location>(context),
            new Repository<Item>(context),
            unitOfWork,
            new DocumentIdentityService(context, unitOfWork, new DocumentNumberService(context, unitOfWork)),
            stockService.Object,
            tenant,
            new Mock<IWebhookDispatcher>().Object,
            NullLogger<TransferOrderService>.Instance);

        var created = await service.CreateAsync(new CreateTransferOrderRequest(
            company.Id,
            source.Id,
            destination.Id,
            [new TransferOrderLineRequest(item.Id, 4), new TransferOrderLineRequest(item.Id, 6)]), "amend-lines-create");
        var originalLines = created.Lines.OrderBy(line => line.Id).ToArray();
        var originalOrder = await context.TransferOrders.SingleAsync(order => order.Id == created.Id);
        originalOrder.Status = TransferOrderStatus.Approved;
        await context.SaveChangesAsync();
        var trackedRemovedLine = context.ChangeTracker.Entries<TransferOrderLine>()
            .Single(entry => entry.Entity.Id == originalLines[1].Id).Entity;
        var detachedRemovedLine = await new Repository<TransferOrderLine>(context).Query()
            .SingleAsync(line => line.Id == originalLines[1].Id);
        detachedRemovedLine.Should().NotBeSameAs(trackedRemovedLine);

        await service.AmendAsync(created.Id, new CreateTransferOrderRequest(
            company.Id,
            source.Id,
            destination.Id,
            [
                new TransferOrderLineRequest(item.Id, 4, LineId: originalLines[0].Id),
                new TransferOrderLineRequest(item.Id, 2)
            ]), new StockMutationScope(company.Id));

        (await context.TransferOrders.SingleAsync(order => order.Id == created.Id)).Status
            .Should().Be(TransferOrderStatus.Draft);
        var persistedLines = await context.TransferOrderLines
            .Where(line => line.TransferOrderId == created.Id)
            .OrderBy(line => line.Id)
            .ToListAsync();
        persistedLines.Should().HaveCount(2);
        var retainedLine = persistedLines.Should().ContainSingle(line => line.Id == originalLines[0].Id).Subject;
        retainedLine.DocumentLineId.Value.Should().Be(originalLines[0].DocumentLineId);
        retainedLine.Quantity.Should().Be(4);
        persistedLines.Should().NotContain(line => line.Id == originalLines[1].Id);
        var addedLine = persistedLines.Single(line => line.Id != originalLines[0].Id);
        addedLine.DocumentLineId.Value.Should().NotBe(originalLines[0].DocumentLineId);
        addedLine.Quantity.Should().Be(2);

        var documentLineIdentities = await context.DocumentLineIdentities
            .Where(identity => identity.DocumentId == new DocumentIdentityId(created.DocumentId))
            .ToListAsync();
        documentLineIdentities.Should().HaveCount(3);
        documentLineIdentities.Should().ContainSingle(identity => identity.Id.Value == addedLine.DocumentLineId.Value);
        documentLineIdentities.Should().ContainSingle(identity => identity.Id.Value == originalLines[1].DocumentLineId);

        foreach (var originalLine in originalLines)
        {
            stockService.Verify(stock => stock.ReleaseReservationAsync(
                originalLine.ReservationSourceLineReference,
                "Transfer order amended; approval invalidated.",
                It.IsAny<StockMutationScope?>()), Times.Once);
        }
    }

    [Fact]
    public async Task Amend_uses_persisted_status_after_another_context_cancels_a_tracked_order()
    {
        var tenant = new TestTenantContext($"transfer-order-amend-stale-{Guid.NewGuid():N}");
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase($"transfer-order-amend-stale-{Guid.NewGuid():N}")
            .Options;
        TransferOrderView created;
        int companyId;
        int sourceLocationId;
        int destinationLocationId;
        int itemId;

        await using (var setup = new InventoryDbContext(options, tenant))
        {
            var company = new Company { Code = "TO-STALE", LegalName = "Stale Order Company" };
            setup.Companies.Add(company);
            await setup.SaveChangesAsync();
            var sourceBranch = new Branch { CompanyId = company.Id, Code = "S", Name = "Source" };
            var destinationBranch = new Branch { CompanyId = company.Id, Code = "D", Name = "Destination" };
            setup.Branches.AddRange(sourceBranch, destinationBranch);
            await setup.SaveChangesAsync();
            var source = new Location { BranchId = sourceBranch.Id, Name = "Source" };
            var destination = new Location { BranchId = destinationBranch.Id, Name = "Destination" };
            var item = new Item { ItemCode = "TO-STALE-ITEM", Description = "Stale order item" };
            setup.Locations.AddRange(source, destination);
            setup.Items.Add(item);
            await setup.SaveChangesAsync();

            companyId = company.Id;
            sourceLocationId = source.Id;
            destinationLocationId = destination.Id;
            itemId = item.Id;
            created = await CreateService(setup, tenant).CreateAsync(
                new CreateTransferOrderRequest(
                    companyId,
                    sourceLocationId,
                    destinationLocationId,
                    [new TransferOrderLineRequest(itemId, 2)]),
                "stale-order-create");
        }

        await using var amendmentContext = new InventoryDbContext(options, tenant);
        var staleOrder = await new Repository<TransferOrder>(amendmentContext).GetByIdAsync(created.Id);
        staleOrder.Should().NotBeNull();
        staleOrder!.Status.Should().Be(TransferOrderStatus.Draft);

        await using (var cancellationContext = new InventoryDbContext(options, tenant))
        {
            await CreateService(cancellationContext, tenant)
                .CancelAsync(created.Id, new StockMutationScope(companyId));
        }

        var amendmentService = CreateService(amendmentContext, tenant);
        await FluentAssertions.FluentActions.Invoking(() => amendmentService.AmendAsync(
                created.Id,
                new CreateTransferOrderRequest(
                    companyId,
                    sourceLocationId,
                    destinationLocationId,
                    [new TransferOrderLineRequest(itemId, 3, LineId: created.Lines.Single().Id)],
                    "do not revive cancellation"),
                new StockMutationScope(companyId)))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Cancelled transfer orders cannot be amended.");

        await using var verify = new InventoryDbContext(options, tenant);
        var persistedOrder = await verify.TransferOrders.SingleAsync(order => order.Id == created.Id);
        persistedOrder.Status.Should().Be(TransferOrderStatus.Cancelled);
        persistedOrder.Notes.Should().BeNull();
        (await verify.TransferOrderLines.SingleAsync(line => line.TransferOrderId == created.Id))
            .Quantity.Should().Be(2);
    }

    [Fact]
    public async Task Create_rejects_an_empty_line_collection_before_persistence()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var service = new TransferOrderService(
            new Mock<IRepository<TransferOrder>>().Object,
            new Mock<IRepository<TransferOrderLine>>().Object,
            new Mock<IRepository<TransferTransitEntry>>().Object,
            new Mock<IRepository<DocumentIdentity>>().Object,
            new Mock<IRepository<DocumentLineIdentity>>().Object,
            new Mock<IRepository<Company>>().Object,
            new Mock<IRepository<Branch>>().Object,
            new Mock<IRepository<Location>>().Object,
            new Mock<IRepository<Item>>().Object,
            unitOfWork.Object,
            new Mock<IDocumentIdentityService>().Object,
            new Mock<IStockService>().Object,
            new TestTenantContext("transfer-order-test"),
            new Mock<IWebhookDispatcher>().Object,
            NullLogger<TransferOrderService>.Instance);

        var action = () => service.CreateAsync(new CreateTransferOrderRequest(
            1, 10, 20, []), "create-1");

        await action.Should().ThrowAsync<ArgumentException>()
            .WithMessage("A transfer order must contain at least one line.*");
        unitOfWork.Verify(uow => uow.ExecuteInTransactionAsync(
            It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>(), It.IsAny<Func<Task<bool>>?>()),
            Times.Never);
    }

    [Fact]
    public async Task Amend_rejects_an_empty_line_collection_before_persistence()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var service = new TransferOrderService(
            new Mock<IRepository<TransferOrder>>().Object,
            new Mock<IRepository<TransferOrderLine>>().Object,
            new Mock<IRepository<TransferTransitEntry>>().Object,
            new Mock<IRepository<DocumentIdentity>>().Object,
            new Mock<IRepository<DocumentLineIdentity>>().Object,
            new Mock<IRepository<Company>>().Object,
            new Mock<IRepository<Branch>>().Object,
            new Mock<IRepository<Location>>().Object,
            new Mock<IRepository<Item>>().Object,
            unitOfWork.Object,
            new Mock<IDocumentIdentityService>().Object,
            new Mock<IStockService>().Object,
            new TestTenantContext("transfer-order-empty-amend-test"),
            new Mock<IWebhookDispatcher>().Object,
            NullLogger<TransferOrderService>.Instance);

        var action = () => service.AmendAsync(
            1,
            new CreateTransferOrderRequest(1, 10, 20, []),
            new StockMutationScope(1));

        await action.Should().ThrowAsync<ArgumentException>()
            .WithMessage("A transfer order must contain at least one line.*");
        unitOfWork.Verify(uow => uow.ExecuteInTransactionAsync(
            It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>(), It.IsAny<Func<Task<bool>>?>()),
            Times.Never);
    }

    [Fact]
    public async Task Amend_acquires_organization_and_location_locks_before_reference_validation()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(uow => uow.ExecuteInTransactionAsync(
                It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>(), It.IsAny<Func<Task<bool>>?>()))
            .Returns((Func<Func<Task>, CancellationToken, Func<Task<bool>>?, Task>)
                ((operation, _, _) => operation()));
        unitOfWork.Setup(uow => uow.AcquireTenantOperationLockAsync("organization-state", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        unitOfWork.Setup(uow => uow.AcquireLocationLocksAsync(
                It.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 10, 20 })),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var companyRepository = new Mock<IRepository<Company>>();
        companyRepository.Setup(repository => repository.FindAsync(It.IsAny<Expression<Func<Company, bool>>>()))
            .ReturnsAsync(Array.Empty<Company>());
        var service = new TransferOrderService(
            new Mock<IRepository<TransferOrder>>().Object,
            new Mock<IRepository<TransferOrderLine>>().Object,
            new Mock<IRepository<TransferTransitEntry>>().Object,
            new Mock<IRepository<DocumentIdentity>>().Object,
            new Mock<IRepository<DocumentLineIdentity>>().Object,
            companyRepository.Object,
            new Mock<IRepository<Branch>>().Object,
            new Mock<IRepository<Location>>().Object,
            new Mock<IRepository<Item>>().Object,
            unitOfWork.Object,
            new Mock<IDocumentIdentityService>().Object,
            new Mock<IStockService>().Object,
            new TestTenantContext("transfer-order-test"),
            new Mock<IWebhookDispatcher>().Object,
            NullLogger<TransferOrderService>.Instance);

        var act = () => service.AmendAsync(
            1,
            new CreateTransferOrderRequest(1, 10, 20, [new TransferOrderLineRequest(1, 1, LineId: 1)]),
            new StockMutationScope(1));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("An active company in the current tenant is required.");
        unitOfWork.Verify(uow => uow.AcquireTenantOperationLockAsync(
            "organization-state", It.IsAny<CancellationToken>()), Times.Once);
        unitOfWork.Verify(uow => uow.AcquireLocationLocksAsync(
            It.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 10, 20 })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Approve_acquires_organization_lock_before_location_lock()
    {
        var events = new List<string>();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(uow => uow.ExecuteInTransactionAsync(
                It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>(), It.IsAny<Func<Task<bool>>?>()))
            .Returns((Func<Func<Task>, CancellationToken, Func<Task<bool>>?, Task>)
                ((operation, _, _) => operation()));
        unitOfWork.Setup(uow => uow.AcquireTenantOperationLockAsync("organization-state", It.IsAny<CancellationToken>()))
            .Callback(() => events.Add("organization"))
            .Returns(Task.CompletedTask);
        unitOfWork.Setup(uow => uow.AcquireLocationLocksAsync(
                It.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 10, 20 })),
                It.IsAny<CancellationToken>()))
            .Callback(() => events.Add("locations"))
            .Returns(Task.CompletedTask);
        var orderRepository = new Mock<IRepository<TransferOrder>>();
        orderRepository.Setup(repository => repository.GetByIdAsync(1))
            .ReturnsAsync(new TransferOrder { Id = 1, FromLocationId = 10, ToLocationId = 20 });
        var lineRepository = new Mock<IRepository<TransferOrderLine>>();
        lineRepository.Setup(repository => repository.FindAsync(It.IsAny<Expression<Func<TransferOrderLine, bool>>>()))
            .ReturnsAsync([new TransferOrderLine { Id = 1, TransferOrderId = 1, ItemId = 1, Quantity = 1 }]);
        var companyRepository = new Mock<IRepository<Company>>();
        companyRepository.Setup(repository => repository.FindAsync(It.IsAny<Expression<Func<Company, bool>>>()))
            .Callback(() => events.Add("validation"))
            .ReturnsAsync(Array.Empty<Company>());
        var service = new TransferOrderService(
            orderRepository.Object,
            lineRepository.Object,
            new Mock<IRepository<TransferTransitEntry>>().Object,
            new Mock<IRepository<DocumentIdentity>>().Object,
            new Mock<IRepository<DocumentLineIdentity>>().Object,
            companyRepository.Object,
            new Mock<IRepository<Branch>>().Object,
            new Mock<IRepository<Location>>().Object,
            new Mock<IRepository<Item>>().Object,
            unitOfWork.Object,
            new Mock<IDocumentIdentityService>().Object,
            new Mock<IStockService>().Object,
            new TestTenantContext("transfer-order-test"),
            new Mock<IWebhookDispatcher>().Object,
            NullLogger<TransferOrderService>.Instance);

        var act = () => service.ApproveAsync(1, new StockMutationScope(1));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("An active company in the current tenant is required.");
        events.Should().Equal("organization", "locations", "validation");
    }

    private static TransferOrderService CreateService(InventoryDbContext context, ITenantContext tenant)
    {
        var unitOfWork = new UnitOfWork(context);
        return new TransferOrderService(
            new Repository<TransferOrder>(context),
            new Repository<TransferOrderLine>(context),
            new Repository<TransferTransitEntry>(context),
            new Repository<DocumentIdentity>(context),
            new Repository<DocumentLineIdentity>(context),
            new Repository<Company>(context),
            new Repository<Branch>(context),
            new Repository<Location>(context),
            new Repository<Item>(context),
            unitOfWork,
            new DocumentIdentityService(context, unitOfWork, new DocumentNumberService(context, unitOfWork)),
            new Mock<IStockService>().Object,
            tenant,
            new Mock<IWebhookDispatcher>().Object,
            NullLogger<TransferOrderService>.Instance);
    }
}
