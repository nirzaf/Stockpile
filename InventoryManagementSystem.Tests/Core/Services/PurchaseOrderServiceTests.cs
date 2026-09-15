using AutoFixture;
using FluentAssertions;
using InventoryManagementSystem.Core.Entities;
using InventoryManagementSystem.Core.Interfaces;
using InventoryManagementSystem.Core.Services;
using InventoryManagementSystem.Tests.Common;
using InventoryManagementSystem.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace InventoryManagementSystem.Tests.Core.Services;

public class PurchaseOrderServiceTests
{
    private readonly Fixture _fixture = InventoryFixtureFactory.Create();
    private readonly Mock<IRepository<PurchaseOrder>> _poRepoMock = new();
    private readonly Mock<IUnitOfWork> _uowMock = new();
    private readonly Mock<IWebhookDispatcher> _webhookDispatcherMock = new();
    private readonly PurchaseOrderService _sut;

    public PurchaseOrderServiceTests()
    {
        _sut = new PurchaseOrderService(
            _poRepoMock.Object,
            _uowMock.Object,
            _webhookDispatcherMock.Object,
            new TestTenantContext("test-tenant"),
            NullLogger<PurchaseOrderService>.Instance);
    }

    [Fact]
    public async Task CreatePurchaseOrderAsync_WhenDetailsAreValid_AddsPurchaseOrderWithPendingStatus()
    {
        // Arrange
        var po = _fixture.Create<PurchaseOrder>();
        po.TotalAmount = 0m;

        var details = new List<OrderDetail>
        {
            new() { ItemId = 1, Quantity = 5, UnitPrice = 10.00m },
            new() { ItemId = 2, Quantity = 3, UnitPrice = 20.00m }
        };

        var expectedTotal = details.Sum(d => d.Quantity * d.UnitPrice);
        _poRepoMock.Setup(r => r.AddAsync(It.IsAny<PurchaseOrder>()))
            .ReturnsAsync((PurchaseOrder p) => p);

        // Act
        var result = await _sut.CreateAsync(po, details);

        // Assert
        result.Status.Should().Be(PurchaseOrderStatus.Pending);
        result.TotalAmount.Should().Be(expectedTotal);
        result.OrderDetails.Should().BeEquivalentTo(details);
        _poRepoMock.Verify(r => r.AddAsync(It.Is<PurchaseOrder>(p =>
            p.Status == PurchaseOrderStatus.Pending &&
            p.TotalAmount == expectedTotal &&
            p.OrderDetails.Count == details.Count)), Times.Once);
        _uowMock.Verify(u => u.SaveChangesAsync(default), Times.Once);
    }

    [Fact]
    public async Task CreatePurchaseOrderAsync_WhenCalled_InvokesAddAsyncExactlyOnce()
    {
        // Arrange
        var po = _fixture.Create<PurchaseOrder>();
        var details = new List<OrderDetail>
        {
            new() { ItemId = 1, Quantity = 1, UnitPrice = 1.00m }
        };
        _poRepoMock.Setup(r => r.AddAsync(It.IsAny<PurchaseOrder>()))
            .ReturnsAsync((PurchaseOrder p) => p);

        // Act
        await _sut.CreateAsync(po, details);

        // Assert
        _poRepoMock.Verify(r => r.AddAsync(It.IsAny<PurchaseOrder>()), Times.Once);
        _uowMock.Verify(u => u.SaveChangesAsync(default), Times.Once);
    }

    [Fact]
    public async Task CreatePurchaseOrderAsync_WhenDetailsAreEmpty_SetsTotalAmountToZero()
    {
        // Arrange
        var po = _fixture.Create<PurchaseOrder>();
        po.TotalAmount = 999m;
        _poRepoMock.Setup(r => r.AddAsync(It.IsAny<PurchaseOrder>()))
            .ReturnsAsync((PurchaseOrder p) => p);

        // Act
        var result = await _sut.CreateAsync(po, new List<OrderDetail>());

        // Assert
        result.TotalAmount.Should().Be(0m);
    }

    [Fact]
    public async Task GetByIdAsync_WhenPurchaseOrderExists_ReturnsPurchaseOrder()
    {
        // Arrange
        var po = _fixture.Create<PurchaseOrder>();
        _poRepoMock.Setup(r => r.GetByIdAsync(po.Id)).ReturnsAsync(po);

        // Act
        var result = await _sut.GetByIdAsync(po.Id);

        // Assert
        result.Should().BeEquivalentTo(po);
    }

    [Fact]
    public async Task GetByIdAsync_WhenPurchaseOrderDoesNotExist_ReturnsNull()
    {
        // Arrange
        var id = _fixture.Create<int>();
        _poRepoMock.Setup(r => r.GetByIdAsync(id)).ReturnsAsync((PurchaseOrder?)null);

        // Act
        var result = await _sut.GetByIdAsync(id);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task UpdateStatusAsync_WhenPurchaseOrderDoesNotExist_ThrowsInvalidOperationException()
    {
        // Arrange
        var id = _fixture.Create<int>();
        _poRepoMock.Setup(r => r.GetByIdAsync(id)).ReturnsAsync((PurchaseOrder?)null);

        // Act
        var act = async () => await _sut.UpdateStatusAsync(id, "Approved");

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Purchase order not found");
    }

    [Fact]
    public async Task UpdateStatusAsync_WhenValidStatus_UpdatesOrderStatus()
    {
        // Arrange
        var po = _fixture.Build<PurchaseOrder>()
            .With(p => p.Status, PurchaseOrderStatus.Pending)
            .Create();
        _poRepoMock.Setup(r => r.GetByIdAsync(po.Id)).ReturnsAsync(po);

        // Act
        await _sut.UpdateStatusAsync(po.Id, "Approved");

        // Assert
        po.Status.Should().Be(PurchaseOrderStatus.Approved);
        _poRepoMock.Verify(r => r.UpdateAsync(po), Times.Once);
        _uowMock.Verify(u => u.SaveChangesAsync(default), Times.Once);
    }

    [Fact]
    public async Task UpdateStatusAsync_WhenStatusChanges_DispatchesNotification()
    {
        // Arrange
        var po = _fixture.Build<PurchaseOrder>()
            .With(p => p.Status, PurchaseOrderStatus.Pending)
            .With(p => p.PONumber, "PO-1001")
            .Create();
        _poRepoMock.Setup(r => r.GetByIdAsync(po.Id)).ReturnsAsync(po);

        // Act
        await _sut.UpdateStatusAsync(po.Id, "Approved");

        // Assert
        var invocation = _webhookDispatcherMock.Invocations
            .Single(i => i.Method.Name == nameof(IWebhookDispatcher.DispatchAsync));
        var webhookEvent = invocation.Arguments[0]!;
        webhookEvent.GetType().GetProperty("EventType")!.GetValue(webhookEvent)
            .Should().Be("PurchaseOrder.StatusChanged");
        webhookEvent.GetType().GetProperty("TenantId")!.GetValue(webhookEvent)
            .Should().Be("test-tenant");
        var payload = webhookEvent.GetType().GetProperty("Payload")!.GetValue(webhookEvent);
        payload.Should().NotBeNull();
        payload!.GetType().GetProperty("PurchaseOrderId")!.GetValue(payload).Should().Be(po.Id);
        payload.GetType().GetProperty("PONumber")!.GetValue(payload).Should().Be("PO-1001");
        payload.GetType().GetProperty("PreviousStatus")!.GetValue(payload).Should().Be("Pending");
        payload.GetType().GetProperty("Status")!.GetValue(payload).Should().Be("Approved");
    }

    [Fact]
    public async Task UpdateStatusAsync_WhenStatusDoesNotChange_DoesNotDispatchNotification()
    {
        // Arrange
        var po = _fixture.Build<PurchaseOrder>()
            .With(p => p.Status, PurchaseOrderStatus.Pending)
            .Create();
        _poRepoMock.Setup(r => r.GetByIdAsync(po.Id)).ReturnsAsync(po);

        // Act
        await _sut.UpdateStatusAsync(po.Id, "Pending");

        // Assert
        _webhookDispatcherMock.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateStatusAsync_WhenInvalidStatus_ThrowsArgumentException()
    {
        // Arrange
        var po = _fixture.Create<PurchaseOrder>();
        _poRepoMock.Setup(r => r.GetByIdAsync(po.Id)).ReturnsAsync(po);

        // Act
        var act = async () => await _sut.UpdateStatusAsync(po.Id, "InvalidStatus");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("Invalid status: InvalidStatus");
    }

    [Fact]
    public async Task DeleteAsync_WhenPurchaseOrderExists_RemovesPurchaseOrder()
    {
        // Arrange
        var po = _fixture.Create<PurchaseOrder>();
        _poRepoMock.Setup(r => r.GetByIdAsync(po.Id)).ReturnsAsync(po);

        // Act
        await _sut.DeleteAsync(po.Id);

        // Assert
        _poRepoMock.Verify(r => r.DeleteAsync(po), Times.Once);
        _uowMock.Verify(u => u.SaveChangesAsync(default), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_WhenPurchaseOrderDoesNotExist_DoesNotInvokeDelete()
    {
        // Arrange
        var id = _fixture.Create<int>();
        _poRepoMock.Setup(r => r.GetByIdAsync(id)).ReturnsAsync((PurchaseOrder?)null);

        // Act
        await _sut.DeleteAsync(id);

        // Assert
        _poRepoMock.Verify(r => r.DeleteAsync(It.IsAny<PurchaseOrder>()), Times.Never);
    }
}
