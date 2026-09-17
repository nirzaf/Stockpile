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
    public async Task Create_rejects_an_empty_line_collection_before_persistence()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var service = new TransferOrderService(
            new Mock<IRepository<TransferOrder>>().Object,
            new Mock<IRepository<TransferOrderLine>>().Object,
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
}
