using AutoFixture;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Services;
using Merconiq.Tests.Common;
using Merconiq.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Merconiq.Tests.Core.Services;

public class PurchaseOrderServiceTests
{
    private readonly Fixture _fixture = InventoryFixtureFactory.Create();
    private readonly Mock<IRepository<PurchaseOrder>> _poRepoMock = new();
    private readonly Mock<IUnitOfWork> _uowMock = new();
    private readonly Mock<IDocumentIdentityService> _documentIdentityMock = new();
    private readonly Mock<IWebhookDispatcher> _webhookDispatcherMock = new();
    private readonly Mock<IRepository<TaxRule>> _taxRuleRepoMock = new();
    private readonly PurchaseOrderService _sut;

    public PurchaseOrderServiceTests()
    {
        _sut = new PurchaseOrderService(
            _poRepoMock.Object,
            _uowMock.Object,
            _documentIdentityMock.Object,
            _webhookDispatcherMock.Object,
            new TestTenantContext("test-tenant"),
            NullLogger<PurchaseOrderService>.Instance,
            _taxRuleRepoMock.Object);

        _documentIdentityMock
            .Setup(service => service.TryReplayPurchaseOrderAsync(
                It.IsAny<PurchaseOrder>(),
                It.IsAny<IReadOnlyCollection<OrderDetail>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PurchaseOrder?)null);
        _documentIdentityMock
            .Setup(service => service.CreatePurchaseOrderAsync(
                It.IsAny<PurchaseOrder>(),
                It.IsAny<IReadOnlyCollection<OrderDetail>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PurchaseOrder order, IReadOnlyCollection<OrderDetail> _, string _, CancellationToken _) => order);
        _documentIdentityMock
            .Setup(service => service.TransitionLifecycleAsync(
                It.IsAny<DocumentIdentityId>(),
                It.IsAny<DocumentLifecycleStatus>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task CreatePurchaseOrderAsync_WhenDetailsAreValid_RegistersPurchaseOrderIdentityAndLines()
    {
        // Arrange
        var po = _fixture.Create<PurchaseOrder>();
        po.TotalAmount = 0m;
        po.CurrencyScale = 2;

        var details = new List<OrderDetail>
        {
            new() { ItemId = 1, Quantity = 5, UnitPrice = 10.00m },
            new() { ItemId = 2, Quantity = 3, UnitPrice = 20.00m }
        };

        var expectedTotal = details.Sum(d => d.Quantity * d.UnitPrice);
        // Act
        var result = await _sut.CreateAsync(po, details, "purchase-order-create-1");

        // Assert
        result.Status.Should().Be(PurchaseOrderStatus.Pending);
        result.TotalAmount.Should().Be(expectedTotal);
        result.OrderDetails.Should().BeEquivalentTo(details);
        _documentIdentityMock.Verify(service => service.CreatePurchaseOrderAsync(
            It.Is<PurchaseOrder>(order => order.Status == PurchaseOrderStatus.Pending &&
                order.TotalAmount == expectedTotal && order.OrderDetails.Count == details.Count),
            It.Is<IReadOnlyCollection<OrderDetail>>(value => value.Count == details.Count),
            "purchase-order-create-1",
            default), Times.Once);
    }

    [Fact]
    public async Task CreatePurchaseOrderAsync_WhenCalled_UsesTheProvidedIdempotencyKeyExactlyOnce()
    {
        // Arrange
        var po = _fixture.Create<PurchaseOrder>();
        po.CurrencyScale = 2;
        var details = new List<OrderDetail>
        {
            new() { ItemId = 1, Quantity = 1, UnitPrice = 1.00m }
        };
        // Act
        await _sut.CreateAsync(po, details, "purchase-order-create-2");

        // Assert
        _documentIdentityMock.Verify(service => service.CreatePurchaseOrderAsync(
            po,
            It.IsAny<IReadOnlyCollection<OrderDetail>>(),
            "purchase-order-create-2",
            default), Times.Once);
    }

    [Fact]
    public async Task CreatePurchaseOrderAsync_WhenDetailsAreEmpty_SetsTotalAmountToZero()
    {
        // Arrange
        var po = _fixture.Create<PurchaseOrder>();
        po.TotalAmount = 999m;
        po.CurrencyScale = 2;
        // Act
        var result = await _sut.CreateAsync(po, new List<OrderDetail>(), "purchase-order-create-3");

        // Assert
        result.TotalAmount.Should().Be(0m);
    }

    [Fact]
    public async Task CreatePurchaseOrderAsync_UsesEffectiveTaxRuleAndPersistsSnapshots()
    {
        var effectiveFrom = DateTime.UtcNow.AddDays(-1);
        var rule = new TaxRule
        {
            Id = 7,
            Code = "STANDARD-15",
            Category = TaxCategory.Standard,
            RatePercent = 15m,
            CalculationMode = TaxCalculationMode.Exclusive,
            EffectiveFromUtc = effectiveFrom,
            IsActive = true
        };
        _taxRuleRepoMock
            .Setup(repository => repository.FindAsync(It.IsAny<System.Linq.Expressions.Expression<Func<TaxRule, bool>>>() ))
            .ReturnsAsync([rule]);

        var po = _fixture.Create<PurchaseOrder>();
        po.CurrencyScale = 2;
        var detail = new OrderDetail
        {
            ItemId = 1,
            Quantity = 1,
            UnitPrice = 100m,
            TaxRuleId = rule.Id
        };

        var result = await _sut.CreateAsync(po, [detail], "purchase-order-tax-rule-1");

        result.TotalAmount.Should().Be(115m);
        result.TaxAmount.Should().Be(15m);
        detail.TaxRatePercent.Should().Be(15m);
        detail.TaxCategory.Should().Be(TaxCategory.Standard);
        detail.TaxEffectiveFromUtc.Should().Be(effectiveFrom);
        detail.TaxableAmount.Should().Be(100m);
        detail.TaxAmount.Should().Be(15m);
        detail.GrossAmount.Should().Be(115m);
        detail.CalculationVersion.Should().Be(DocumentAmountCalculator.CalculationVersion);
    }

    [Fact]
    public async Task CreatePurchaseOrderAsync_ReplaysBeforeResolvingAnExpiredTaxRule()
    {
        var existingOrder = new PurchaseOrder
        {
            Id = 98,
            PONumber = "EXISTING-98",
            SupplierId = 5,
            Status = PurchaseOrderStatus.Pending,
            TotalAmount = 115m,
            TaxAmount = 15m
        };
        _documentIdentityMock
            .Setup(service => service.TryReplayPurchaseOrderAsync(
                It.IsAny<PurchaseOrder>(),
                It.IsAny<IReadOnlyCollection<OrderDetail>>(),
                "expired-rule-retry",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingOrder);
        var order = new PurchaseOrder { PONumber = "EXISTING-98", SupplierId = 5, CurrencyScale = 3 };
        var detail = new OrderDetail { ItemId = 1, Quantity = 1, UnitPrice = 100m, TaxRuleId = 14 };

        var replay = await _sut.CreateAsync(order, [detail], "expired-rule-retry");

        replay.Should().BeSameAs(existingOrder);
        detail.CurrencyScale.Should().Be(3);
        _documentIdentityMock.Verify(service => service.TryReplayPurchaseOrderAsync(
            It.IsAny<PurchaseOrder>(),
            It.Is<IReadOnlyCollection<OrderDetail>>(lines => lines.Count == 1 && lines.Single().CurrencyScale == 3),
            "expired-rule-retry",
            It.IsAny<CancellationToken>()), Times.Once);
        _taxRuleRepoMock.Verify(repository => repository.FindAsync(
            It.IsAny<System.Linq.Expressions.Expression<Func<TaxRule, bool>>>()), Times.Never);
        _documentIdentityMock.Verify(service => service.CreatePurchaseOrderAsync(
            It.IsAny<PurchaseOrder>(),
            It.IsAny<IReadOnlyCollection<OrderDetail>>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreatePurchaseOrderAsync_RejectsTaxRuleOutsideItsEffectivePeriod()
    {
        _taxRuleRepoMock
            .Setup(repository => repository.FindAsync(It.IsAny<System.Linq.Expressions.Expression<Func<TaxRule, bool>>>() ))
            .ReturnsAsync([
                new TaxRule
                {
                    Id = 8,
                    Code = "FUTURE",
                    Category = TaxCategory.Standard,
                    RatePercent = 5m,
                    EffectiveFromUtc = DateTime.UtcNow.AddDays(1),
                    IsActive = true
                }
            ]);

        var po = _fixture.Create<PurchaseOrder>();
        po.CurrencyScale = 2;
        var detail = new OrderDetail { ItemId = 1, Quantity = 1, UnitPrice = 10m, TaxRuleId = 8 };

        var act = () => _sut.CreateAsync(po, [detail], "purchase-order-tax-rule-2");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Tax rule 8 is not active at *.");
    }

    [Fact]
    public async Task CreatePurchaseOrderAsync_RejectsAnUnconfiguredNonZeroTaxRate()
    {
        var po = _fixture.Create<PurchaseOrder>();
        po.CurrencyScale = 2;
        var detail = new OrderDetail { ItemId = 1, Quantity = 1, UnitPrice = 10m, TaxRatePercent = 5m };

        var act = () => _sut.CreateAsync(po, [detail], "purchase-order-tax-rule-3");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("A configured tax rule is required for a non-zero tax rate.");
        _documentIdentityMock.Verify(service => service.CreatePurchaseOrderAsync(
            It.IsAny<PurchaseOrder>(),
            It.IsAny<IReadOnlyCollection<OrderDetail>>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
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
            .Without(p => p.DocumentIdentity)
            .With(p => p.Status, PurchaseOrderStatus.Pending)
            .Create();
        _poRepoMock.Setup(r => r.GetByIdAsync(po.Id)).ReturnsAsync(po);

        // Act
        await _sut.UpdateStatusAsync(po.Id, "Approved");

        // Assert
        po.Status.Should().Be(PurchaseOrderStatus.Approved);
        _poRepoMock.Verify(r => r.UpdateAsync(It.IsAny<PurchaseOrder>()), Times.Never);
        _uowMock.Verify(u => u.SaveChangesAsync(default), Times.Once);
    }

    [Fact]
    public async Task UpdateStatusAsync_WhenDraftMovesToPending_UpdatesOrderStatus()
    {
        var po = _fixture.Build<PurchaseOrder>()
            .Without(p => p.DocumentIdentity)
            .With(p => p.Status, PurchaseOrderStatus.Draft)
            .Create();
        _poRepoMock.Setup(r => r.GetByIdAsync(po.Id)).ReturnsAsync(po);

        await _sut.UpdateStatusAsync(po.Id, nameof(PurchaseOrderStatus.Pending));

        po.Status.Should().Be(PurchaseOrderStatus.Pending);
        _poRepoMock.Verify(r => r.UpdateAsync(It.IsAny<PurchaseOrder>()), Times.Never);
    }

    [Theory]
    [InlineData(PurchaseOrderStatus.Pending, PurchaseOrderStatus.Received)]
    [InlineData(PurchaseOrderStatus.Approved, PurchaseOrderStatus.Submitted)]
    [InlineData(PurchaseOrderStatus.Received, PurchaseOrderStatus.Cancelled)]
    [InlineData(PurchaseOrderStatus.Cancelled, PurchaseOrderStatus.Pending)]
    public async Task UpdateStatusAsync_WhenTransitionIsNotAllowed_RejectsWithoutMutation(
        PurchaseOrderStatus current,
        PurchaseOrderStatus requested)
    {
        var po = _fixture.Build<PurchaseOrder>().Without(p => p.DocumentIdentity).With(p => p.Status, current).Create();
        _poRepoMock.Setup(r => r.GetByIdAsync(po.Id)).ReturnsAsync(po);

        var act = async () => await _sut.UpdateStatusAsync(po.Id, requested.ToString());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"Invalid purchase order status transition: {current} -> {requested}");
        po.Status.Should().Be(current);
        _poRepoMock.Verify(r => r.UpdateAsync(It.IsAny<PurchaseOrder>()), Times.Never);
        _uowMock.Verify(u => u.SaveChangesAsync(default), Times.Never);
        _webhookDispatcherMock.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateStatusAsync_WhenStatusChanges_QueuesNotification()
    {
        // Arrange
        var po = _fixture.Build<PurchaseOrder>()
            .Without(p => p.DocumentIdentity)
            .With(p => p.Status, PurchaseOrderStatus.Pending)
            .With(p => p.PONumber, "PO-1001")
            .Create();
        _poRepoMock.Setup(r => r.GetByIdAsync(po.Id)).ReturnsAsync(po);

        // Act
        await _sut.UpdateStatusAsync(po.Id, "Approved");

        // Assert
        var invocation = _webhookDispatcherMock.Invocations
            .Single(i => i.Method.Name == nameof(IWebhookDispatcher.EnqueueAsync));
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
    public async Task UpdateStatusAsync_WhenStatusDoesNotChange_DoesNotQueueNotification()
    {
        // Arrange
        var po = _fixture.Build<PurchaseOrder>()
            .Without(p => p.DocumentIdentity)
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

    [Theory]
    [InlineData(PurchaseOrderStatus.Draft)]
    [InlineData(PurchaseOrderStatus.Pending)]
    public async Task DeleteAsync_WhenPurchaseOrderIsEditable_CancelsAndRetainsPurchaseOrder(PurchaseOrderStatus status)
    {
        // Arrange
        var po = _fixture.Build<PurchaseOrder>().Without(p => p.DocumentIdentity).With(p => p.Status, status).Create();
        _poRepoMock.Setup(r => r.GetByIdAsync(po.Id)).ReturnsAsync(po);

        // Act
        await _sut.DeleteAsync(po.Id);

        // Assert
        po.Status.Should().Be(PurchaseOrderStatus.Cancelled);
        _poRepoMock.Verify(r => r.DeleteAsync(It.IsAny<PurchaseOrder>()), Times.Never);
        _poRepoMock.Verify(r => r.UpdateAsync(It.IsAny<PurchaseOrder>()), Times.Never);
        _documentIdentityMock.Verify(service => service.TransitionLifecycleAsync(
            po.DocumentId,
            DocumentLifecycleStatus.Cancelled,
            default), Times.Once);
        _uowMock.Verify(u => u.SaveChangesAsync(default), Times.Once);
    }

    [Theory]
    [InlineData(PurchaseOrderStatus.Submitted)]
    [InlineData(PurchaseOrderStatus.Approved)]
    public async Task UpdateStatusAsync_WhenAnIssuedOrderIsVoided_RetainsItsDocumentIdentity(PurchaseOrderStatus current)
    {
        var po = _fixture.Build<PurchaseOrder>().Without(order => order.DocumentIdentity).With(order => order.Status, current).Create();
        _poRepoMock.Setup(repository => repository.GetByIdAsync(po.Id)).ReturnsAsync(po);

        await _sut.UpdateStatusAsync(po.Id, nameof(PurchaseOrderStatus.Voided));

        po.Status.Should().Be(PurchaseOrderStatus.Voided);
        _documentIdentityMock.Verify(service => service.TransitionLifecycleAsync(
            po.DocumentId,
            DocumentLifecycleStatus.Voided,
            default), Times.Once);
        _poRepoMock.Verify(repository => repository.DeleteAsync(It.IsAny<PurchaseOrder>()), Times.Never);
    }

    [Fact]
    public async Task UpdateStatusAsync_WhenReceivedOrderIsVoided_RejectsBecauseNoReversalExists()
    {
        var po = _fixture.Build<PurchaseOrder>().Without(order => order.DocumentIdentity).With(order => order.Status, PurchaseOrderStatus.Received).Create();
        _poRepoMock.Setup(repository => repository.GetByIdAsync(po.Id)).ReturnsAsync(po);

        var act = () => _sut.UpdateStatusAsync(po.Id, nameof(PurchaseOrderStatus.Voided));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Invalid purchase order status transition: Received -> Voided");
        po.Status.Should().Be(PurchaseOrderStatus.Received);
        _documentIdentityMock.Verify(service => service.TransitionLifecycleAsync(
            It.IsAny<DocumentIdentityId>(),
            It.IsAny<DocumentLifecycleStatus>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(PurchaseOrderStatus.Submitted)]
    [InlineData(PurchaseOrderStatus.Approved)]
    [InlineData(PurchaseOrderStatus.Received)]
    [InlineData(PurchaseOrderStatus.Cancelled)]
    public async Task DeleteAsync_WhenPurchaseOrderIsProtected_RejectsWithoutMutation(PurchaseOrderStatus status)
    {
        var po = _fixture.Build<PurchaseOrder>().Without(p => p.DocumentIdentity).With(p => p.Status, status).Create();
        _poRepoMock.Setup(r => r.GetByIdAsync(po.Id)).ReturnsAsync(po);

        var act = async () => await _sut.DeleteAsync(po.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"Purchase order {po.Id} in status {status} cannot be deleted.");
        _poRepoMock.Verify(r => r.DeleteAsync(It.IsAny<PurchaseOrder>()), Times.Never);
        _uowMock.Verify(u => u.SaveChangesAsync(default), Times.Never);
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
