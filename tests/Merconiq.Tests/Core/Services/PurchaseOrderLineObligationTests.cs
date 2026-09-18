using System.Linq.Expressions;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Services;
using Merconiq.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Merconiq.Tests.Core.Services;

public sealed class PurchaseOrderLineObligationTests
{
    [Fact]
    public async Task GetLineObligationsAsync_ReportsOnlyOrderedChargeQuantitiesInOneReadSnapshot()
    {
        var order = new PurchaseOrder { Id = 11, PONumber = "PO-OBLIGATIONS-11" };
        var charge = new OrderDetail
        {
            Id = 15,
            PurchaseOrderId = order.Id,
            ItemId = 7,
            Quantity = 5,
            Direction = DocumentLineDirection.Charge
        };
        var reversal = new OrderDetail
        {
            Id = 16,
            PurchaseOrderId = order.Id,
            ItemId = 8,
            Quantity = -2,
            Direction = DocumentLineDirection.Reversal
        };
        var lines = new[] { charge, reversal };
        var orderRepository = new Mock<IRepository<PurchaseOrder>>();
        var lineRepository = new Mock<IRepository<OrderDetail>>();
        var itemRepository = new Mock<IRepository<Item>>();
        var unitOfWork = new Mock<IUnitOfWork>();
        var documentIdentity = new Mock<IDocumentIdentityService>();
        var webhooks = new Mock<IWebhookDispatcher>();
        var auditLogs = new Mock<IRepository<AuditLog>>();
        var snapshotInvoked = false;

        orderRepository.Setup(repository => repository.GetByIdAsync(order.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(order);
        lineRepository.Setup(repository => repository.FindAsync(
                It.IsAny<Expression<Func<OrderDetail, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<OrderDetail, bool>> predicate, CancellationToken _) =>
                lines.Where(predicate.Compile()).ToArray());
        var items = new[]
        {
            new Item { Id = 7, ItemCode = "ITEM-7", Description = "Charge" },
            new Item { Id = 8, ItemCode = "ITEM-8", Description = "Reversal" }
        };
        itemRepository.Setup(repository => repository.FindAsync(
                It.IsAny<Expression<Func<Item, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<Item, bool>> predicate, CancellationToken _) =>
                items.Where(predicate.Compile()).ToArray());
        unitOfWork.Setup(work => work.ExecuteInReadSnapshotAsync(
                It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>()))
            .Returns((Func<Task> operation, CancellationToken _) =>
            {
                snapshotInvoked = true;
                return operation();
            });

        var service = new PurchaseOrderService(
            orderRepository.Object,
            unitOfWork.Object,
            documentIdentity.Object,
            webhooks.Object,
            new TestTenantContext("obligations-tenant"),
            NullLogger<PurchaseOrderService>.Instance,
            auditLogs.Object,
            orderDetailRepository: lineRepository.Object,
            itemRepository: itemRepository.Object);

        var obligations = await service.GetLineObligationsAsync(order.Id);

        obligations.Should().NotBeNull();
        obligations!.OrderedQuantity.Should().Be(5);
        obligations.Lines.Should().ContainSingle();
        obligations.Lines[0].Should().BeEquivalentTo(new
        {
            LineId = charge.Id,
            ItemId = charge.ItemId,
            ItemCode = "ITEM-7",
            ItemDescription = "Charge",
            OrderedQuantity = 5
        });
        snapshotInvoked.Should().BeTrue();
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        unitOfWork.Verify(work => work.SaveChangesAsAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
