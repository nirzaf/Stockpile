using System.Linq.Expressions;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Core.Services;
using Merconiq.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Merconiq.Tests.Core.Services;

public sealed class PurchaseOrderLineProgressTests
{
    private static readonly PurchaseOrderStatusActor TestActor = new("progress-user", "Progress User");

    [Fact]
    public void RecordReceivingOutcome_TracksAcceptedRejectedPendingAndOutstandingQuantities()
    {
        var line = new OrderDetail { Quantity = 8 };

        line.RecordReceivingOutcome(receivedQuantity: 5, acceptedQuantity: 3, rejectedQuantity: 1);

        line.ReceivedQuantity.Should().Be(5);
        line.AcceptedQuantity.Should().Be(3);
        line.RejectedQuantity.Should().Be(1);
        line.AwaitingInspectionQuantity.Should().Be(1);
        line.OutstandingQuantity.Should().Be(3);
    }

    [Fact]
    public void RecordReceivingOutcome_RejectsOverReceiptWithoutChangingAnyLineQuantity()
    {
        var line = new OrderDetail { Quantity = 4 };
        line.RecordReceivingOutcome(receivedQuantity: 2, acceptedQuantity: 1, rejectedQuantity: 0);

        var act = () => line.RecordReceivingOutcome(receivedQuantity: 3, acceptedQuantity: 0, rejectedQuantity: 0);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("The receiving quantity cannot exceed the ordered quantity.");
        line.ReceivedQuantity.Should().Be(2);
        line.AcceptedQuantity.Should().Be(1);
        line.RejectedQuantity.Should().Be(0);
    }

    [Fact]
    public void RecordReceivingOutcome_RejectsClassificationBeyondPreviouslyReceivedPendingQuantity()
    {
        var line = new OrderDetail { Quantity = 4 };
        line.RecordReceivingOutcome(receivedQuantity: 3, acceptedQuantity: 1, rejectedQuantity: 1);

        var act = () => line.RecordReceivingOutcome(receivedQuantity: 0, acceptedQuantity: 1, rejectedQuantity: 1);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Accepted and rejected quantities cannot exceed the received quantity.");
        line.AcceptedQuantity.Should().Be(1);
        line.RejectedQuantity.Should().Be(1);
        line.AwaitingInspectionQuantity.Should().Be(1);
    }

    [Fact]
    public async Task RecordLineProgressAsync_UpdatesApprovedLineAndRevisionButKeepsLifecycleApproved()
    {
        var order = ApprovedOrder();
        var line = new OrderDetail { Id = 12, PurchaseOrderId = order.Id, ItemId = 7, Quantity = 8 };
        var (service, orderRepository, lineRepository, unitOfWork, _) = CreateService(order, line);

        await service.RecordLineProgressAsync(
            order.Id,
            line.Id,
            new PurchaseOrderLineProgressChange(5, 3, 1),
            TestActor);

        order.Status.Should().Be(PurchaseOrderStatus.Approved);
        order.ReceivingRevision.Should().Be(1);
        line.ReceivedQuantity.Should().Be(5);
        line.AcceptedQuantity.Should().Be(3);
        line.RejectedQuantity.Should().Be(1);
        unitOfWork.Verify(work => work.SaveChangesAsAsync(TestActor.AuditUsername, default), Times.Once);
        orderRepository.Verify(repository => repository.GetByIdAsync(order.Id, default), Times.Once);
        lineRepository.Verify(repository => repository.GetByIdAsync(line.Id, default), Times.Once);
    }

    [Fact]
    public async Task RecordLineProgressAsync_RejectsOverReceiptBeforeMutationOrSave()
    {
        var order = ApprovedOrder();
        var line = new OrderDetail { Id = 13, PurchaseOrderId = order.Id, ItemId = 7, Quantity = 4 };
        var (service, _, _, unitOfWork, _) = CreateService(order, line);

        var act = () => service.RecordLineProgressAsync(
            order.Id,
            line.Id,
            new PurchaseOrderLineProgressChange(5, 0, 0),
            TestActor);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The receiving quantity cannot exceed the ordered quantity.");
        line.ReceivedQuantity.Should().Be(0);
        order.ReceivingRevision.Should().Be(0);
        unitOfWork.Verify(work => work.SaveChangesAsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecordLineProgressAsync_RejectsNonApprovedLifecycleBeforeLoadingOrMutatingLine()
    {
        var order = ApprovedOrder();
        order.Status = PurchaseOrderStatus.Pending;
        var line = new OrderDetail { Id = 14, PurchaseOrderId = order.Id, Quantity = 4 };
        var (service, _, lineRepository, unitOfWork, _) = CreateService(order, line);

        var act = () => service.RecordLineProgressAsync(
            order.Id,
            line.Id,
            new PurchaseOrderLineProgressChange(1, 1, 0),
            TestActor);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Line progress can only be recorded against an approved purchase order.");
        lineRepository.Verify(repository => repository.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        line.ReceivedQuantity.Should().Be(0);
        order.ReceivingRevision.Should().Be(0);
        unitOfWork.Verify(work => work.SaveChangesAsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(1, 0, 1, PurchaseOrderProgressState.InspectionPending)]
    [InlineData(1, 1, 0, PurchaseOrderProgressState.AllReceivedAndClassified)]
    public async Task GetReceivingProgressAsync_DerivesLineStateWithoutChangingPurchaseOrderLifecycle(
        int accepted,
        int rejected,
        int expectedAwaitingInspection,
        PurchaseOrderProgressState expectedState)
    {
        var order = ApprovedOrder();
        var line = new OrderDetail { Id = 15, PurchaseOrderId = order.Id, ItemId = 7, Quantity = 2 };
        line.RecordReceivingOutcome(2, accepted, rejected);
        var (service, orderRepository, lineRepository, _, itemRepository) = CreateService(order, line);
        orderRepository.Setup(repository => repository.GetByIdAsync(order.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(order);
        lineRepository.Setup(repository => repository.FindAsync(
                It.IsAny<Expression<Func<OrderDetail, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<OrderDetail, bool>> predicate, CancellationToken _) =>
                new[] { line }.Where(predicate.Compile()).ToArray());
        var item = new Item { Id = 7, ItemCode = "ITEM-7", Description = "Test item" };
        itemRepository.Setup(repository => repository.FindAsync(
                It.IsAny<Expression<Func<Item, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<Item, bool>> predicate, CancellationToken _) =>
                new[] { item }.Where(predicate.Compile()).ToArray());

        var progress = await service.GetReceivingProgressAsync(order.Id);

        progress.Should().NotBeNull();
        progress!.LifecycleStatus.Should().Be(PurchaseOrderStatus.Approved);
        progress.ProgressState.Should().Be(expectedState);
        progress.OrderedQuantity.Should().Be(2);
        progress.ReceivedQuantity.Should().Be(2);
        progress.AcceptedQuantity.Should().Be(accepted);
        progress.RejectedQuantity.Should().Be(rejected);
        progress.OutstandingQuantity.Should().Be(0);
        progress.AwaitingInspectionQuantity.Should().Be(expectedAwaitingInspection);
        progress.Lines.Should().ContainSingle().Which.ItemCode.Should().Be("ITEM-7");
        order.Status.Should().Be(PurchaseOrderStatus.Approved);
    }

    private static PurchaseOrder ApprovedOrder() => new()
    {
        Id = 11,
        PONumber = "PO-PROGRESS-11",
        Status = PurchaseOrderStatus.Approved,
        CommercialVersion = 2,
        ApprovedCommercialVersion = 2,
        ApprovedCommercialSnapshotJson = "{\"schemaVersion\":1}"
    };

    private static (
        PurchaseOrderService Service,
        Mock<IRepository<PurchaseOrder>> OrderRepository,
        Mock<IRepository<OrderDetail>> LineRepository,
        Mock<IUnitOfWork> UnitOfWork,
        Mock<IRepository<Item>> ItemRepository) CreateService(PurchaseOrder order, OrderDetail line)
    {
        var orderRepository = new Mock<IRepository<PurchaseOrder>>();
        var lineRepository = new Mock<IRepository<OrderDetail>>();
        var itemRepository = new Mock<IRepository<Item>>();
        var unitOfWork = new Mock<IUnitOfWork>();
        var documentIdentity = new Mock<IDocumentIdentityService>();
        var webhooks = new Mock<IWebhookDispatcher>();
        var auditLogs = new Mock<IRepository<AuditLog>>();

        orderRepository.Setup(repository => repository.GetByIdAsync(order.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(order);
        lineRepository.Setup(repository => repository.GetByIdAsync(line.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(line);
        unitOfWork.Setup(work => work.SaveChangesAsAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var service = new PurchaseOrderService(
            orderRepository.Object,
            unitOfWork.Object,
            documentIdentity.Object,
            webhooks.Object,
            new TestTenantContext("progress-tenant"),
            NullLogger<PurchaseOrderService>.Instance,
            auditLogs.Object,
            orderDetailRepository: lineRepository.Object,
            itemRepository: itemRepository.Object);
        return (service, orderRepository, lineRepository, unitOfWork, itemRepository);
    }
}
