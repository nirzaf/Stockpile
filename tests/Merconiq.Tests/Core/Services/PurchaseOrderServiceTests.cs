using System.Linq.Expressions;
using AutoFixture;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
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
    private readonly Mock<IRepository<OrderDetail>> _orderDetailRepoMock = new();
    private readonly Mock<IRepository<Supplier>> _supplierRepoMock = new();
    private readonly Mock<IRepository<Item>> _itemRepoMock = new();
    private readonly Mock<IRepository<UnitOfMeasure>> _unitRepoMock = new();
    private readonly Mock<IRepository<DocumentIdentity>> _documentRepoMock = new();
    private readonly Mock<IRepository<AuditLog>> _auditLogRepoMock = new();
    private static readonly PurchaseOrderStatusActor TestActor = new("test-user-id", "Test User");
    private readonly PurchaseOrderService _sut;
    private List<OrderDetail> _testLines = [];
    private List<Supplier> _testSuppliers = [];
    private List<Item> _testItems = [];
    private List<UnitOfMeasure> _testUnits = [];
    private List<DocumentIdentity> _testDocuments = [];

    public PurchaseOrderServiceTests()
    {
        _sut = new PurchaseOrderService(
            _poRepoMock.Object,
            _uowMock.Object,
            _documentIdentityMock.Object,
            _webhookDispatcherMock.Object,
            new TestTenantContext("test-tenant"),
            NullLogger<PurchaseOrderService>.Instance,
            _auditLogRepoMock.Object,
            _taxRuleRepoMock.Object,
            _orderDetailRepoMock.Object,
            _supplierRepoMock.Object,
            _itemRepoMock.Object,
            _unitRepoMock.Object,
            _documentRepoMock.Object);

        _orderDetailRepoMock.Setup(repository => repository.FindAsync(It.IsAny<Expression<Func<OrderDetail, bool>>>() ))
            .ReturnsAsync((Expression<Func<OrderDetail, bool>> predicate) => _testLines.Where(predicate.Compile()).ToArray());
        _supplierRepoMock.Setup(repository => repository.FindAsync(It.IsAny<Expression<Func<Supplier, bool>>>() ))
            .ReturnsAsync((Expression<Func<Supplier, bool>> predicate) => _testSuppliers.Where(predicate.Compile()).ToArray());
        _itemRepoMock.Setup(repository => repository.FindAsync(It.IsAny<Expression<Func<Item, bool>>>() ))
            .ReturnsAsync((Expression<Func<Item, bool>> predicate) => _testItems.Where(predicate.Compile()).ToArray());
        _unitRepoMock.Setup(repository => repository.FindAsync(It.IsAny<Expression<Func<UnitOfMeasure, bool>>>() ))
            .ReturnsAsync((Expression<Func<UnitOfMeasure, bool>> predicate) => _testUnits.Where(predicate.Compile()).ToArray());
        _documentRepoMock.Setup(repository => repository.FindAsync(It.IsAny<Expression<Func<DocumentIdentity, bool>>>() ))
            .ReturnsAsync((Expression<Func<DocumentIdentity, bool>> predicate) => _testDocuments.Where(predicate.Compile()).ToArray());

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
        _uowMock
            .Setup(unitOfWork => unitOfWork.SaveChangesAsAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        _uowMock
            .Setup(unitOfWork => unitOfWork.ExecuteInReadSnapshotAsync(
                It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>()))
            .Returns((Func<Task> operation, CancellationToken _) => operation());
        _uowMock
            .Setup(uow => uow.ExecuteInTransactionAsync(
                It.IsAny<Func<Task>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Func<Task<bool>>?>()))
            .Returns((Func<Task> operation, CancellationToken _, Func<Task<bool>>? _) => operation());
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
        result.CommercialVersion.Should().Be(1);
        result.ApprovedCommercialVersion.Should().BeNull();
        result.ApprovedCommercialSnapshotJson.Should().BeNull();
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
    public async Task CreatePurchaseOrderAsync_WhenLineQuantityIsZero_RejectsBeforeChangingOrderOrCreatingIdentity()
    {
        var order = new PurchaseOrder
        {
            PONumber = "PO-ZERO-QUANTITY",
            SupplierId = 1,
            CurrencyScale = 2,
            Status = PurchaseOrderStatus.Draft
        };
        var detail = new OrderDetail { ItemId = 1, Quantity = 0, UnitPrice = 10m };

        var act = () => _sut.CreateAsync(order, [detail], "purchase-order-create-zero-quantity");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Purchase-order line quantity must be greater than zero.");
        order.Status.Should().Be(PurchaseOrderStatus.Draft);
        order.OrderDate.Should().Be(default);
        _documentIdentityMock.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task CreatePurchaseOrderAsync_RejectsUnitPriceBeyondPersistedScale()
    {
        var order = new PurchaseOrder { PONumber = "PO-PRECISION", SupplierId = 1, CurrencyScale = 4 };
        var detail = new OrderDetail { ItemId = 1, Quantity = 1, UnitPrice = 1.23456m };

        var act = () => _sut.CreateAsync(order, [detail], "purchase-order-create-precision");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Unit price must fit within decimal(20,4) storage precision.");
        _documentIdentityMock.Verify(service => service.TryReplayPurchaseOrderAsync(
            It.IsAny<PurchaseOrder>(), It.IsAny<IReadOnlyCollection<OrderDetail>>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
        var act = async () => await _sut.UpdateStatusAsync(id, "Approved", TestActor);

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
        SetupSnapshotData(po, new OrderDetail { Id = 1, PurchaseOrderId = po.Id, ItemId = 10, Quantity = 2, UnitPrice = 4m });

        // Act
        await _sut.UpdateStatusAsync(po.Id, "Approved", TestActor);

        // Assert
        po.Status.Should().Be(PurchaseOrderStatus.Approved);
        po.ApprovedCommercialVersion.Should().Be(po.CommercialVersion);
        po.ApprovedCommercialSnapshotJson.Should().Contain("\"supplierName\"");
        _poRepoMock.Verify(r => r.UpdateAsync(It.IsAny<PurchaseOrder>()), Times.Never);
        _uowMock.Verify(u => u.SaveChangesAsAsync(TestActor.AuditUsername, default), Times.Once);
    }

    [Fact]
    public async Task UpdateStatusAsync_WhenApprovingAnEmptyPurchaseOrder_RejectsWithoutMutation()
    {
        var po = new PurchaseOrder
        {
            Id = 902,
            PONumber = "PO-EMPTY-902",
            Status = PurchaseOrderStatus.Pending,
            CommercialVersion = 1
        };
        _poRepoMock.Setup(repository => repository.GetByIdAsync(po.Id)).ReturnsAsync(po);

        var act = () => _sut.UpdateStatusAsync(po.Id, nameof(PurchaseOrderStatus.Approved), TestActor);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("A purchase order must contain at least one line before it can be approved.");
        po.Status.Should().Be(PurchaseOrderStatus.Pending);
        po.ApprovedCommercialVersion.Should().BeNull();
        po.ApprovedCommercialSnapshotJson.Should().BeNull();
        _uowMock.Verify(unitOfWork => unitOfWork.SaveChangesAsync(default), Times.Never);
        _webhookDispatcherMock.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateStatusAsync_WhenDraftMovesToPending_UpdatesOrderStatus()
    {
        var po = _fixture.Build<PurchaseOrder>()
            .Without(p => p.DocumentIdentity)
            .With(p => p.Status, PurchaseOrderStatus.Draft)
            .Create();
        _poRepoMock.Setup(r => r.GetByIdAsync(po.Id)).ReturnsAsync(po);

        await _sut.UpdateStatusAsync(po.Id, nameof(PurchaseOrderStatus.Pending), TestActor);

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

        var act = async () => await _sut.UpdateStatusAsync(po.Id, requested.ToString(), TestActor);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"Invalid purchase order status transition: {current} -> {requested}");
        po.Status.Should().Be(current);
        _poRepoMock.Verify(r => r.UpdateAsync(It.IsAny<PurchaseOrder>()), Times.Never);
        _uowMock.Verify(u => u.SaveChangesAsync(default), Times.Never);
        _webhookDispatcherMock.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateStatusAsync_WhenApprovedOrderIsMarkedReceivedWithoutReceipt_RejectsWithoutChangingOrderOrDocumentLifecycle()
    {
        var po = _fixture.Build<PurchaseOrder>()
            .Without(order => order.DocumentIdentity)
            .With(order => order.Status, PurchaseOrderStatus.Approved)
            .With(order => order.PONumber, "PO-RECEIPT-TEST")
            .Create();
        var documentIdentity = DocumentIdentity.Create(
            po.DocumentId,
            "test-tenant",
            companyId: null,
            documentType: "PurchaseOrder",
            humanNumber: po.PONumber,
            period: 2026,
            status: DocumentLifecycleStatus.Active,
            requestScope: "unit-test");
        po.DocumentIdentity = documentIdentity;
        _poRepoMock.Setup(repository => repository.GetByIdAsync(po.Id)).ReturnsAsync(po);

        var act = () => _sut.UpdateStatusAsync(po.Id, nameof(PurchaseOrderStatus.Received), TestActor);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Invalid purchase order status transition: Approved -> Received");
        po.Status.Should().Be(PurchaseOrderStatus.Approved);
        documentIdentity.Status.Should().Be(DocumentLifecycleStatus.Active);
        _documentIdentityMock.Verify(service => service.TransitionLifecycleAsync(
            It.IsAny<DocumentIdentityId>(),
            It.IsAny<DocumentLifecycleStatus>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _uowMock.Verify(unitOfWork => unitOfWork.SaveChangesAsync(default), Times.Never);
        _webhookDispatcherMock.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateStatusAsync_WhenCommercialVersionChangesAfterApprovalStarts_RejectsStaleRequest()
    {
        var po = _fixture.Build<PurchaseOrder>()
            .Without(p => p.DocumentIdentity)
            .With(p => p.Status, PurchaseOrderStatus.Pending)
            .With(p => p.CommercialVersion, 1)
            .With(p => p.ApprovedCommercialVersion, (int?)null)
            .With(p => p.ApprovedCommercialSnapshotJson, (string?)null)
            .Create();
        var reads = 0;
        _poRepoMock.Setup(repository => repository.GetByIdAsync(po.Id))
            .ReturnsAsync(() =>
            {
                if (Interlocked.Increment(ref reads) == 2)
                {
                    po.CommercialVersion++;
                }

                return po;
            });

        var act = () => _sut.UpdateStatusAsync(po.Id, nameof(PurchaseOrderStatus.Approved), TestActor);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Purchase order changed after approval started; review the latest commercial version before approving.");
        po.Status.Should().Be(PurchaseOrderStatus.Pending);
        po.ApprovedCommercialSnapshotJson.Should().BeNull();
        _uowMock.Verify(unitOfWork => unitOfWork.SaveChangesAsync(default), Times.Never);
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
        SetupSnapshotData(po, new OrderDetail { Id = 1, PurchaseOrderId = po.Id, ItemId = 10, Quantity = 2, UnitPrice = 4m });

        // Act
        await _sut.UpdateStatusAsync(po.Id, "Approved", TestActor);

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
        await _sut.UpdateStatusAsync(po.Id, "Pending", TestActor);

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
        var act = async () => await _sut.UpdateStatusAsync(po.Id, "InvalidStatus", TestActor);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("Invalid status: InvalidStatus");
    }

    [Fact]
    public async Task AmendApprovedAsync_WhenCommercialTermsChange_InvalidatesApprovalAndPreservesLineIdentity()
    {
        var po = new PurchaseOrder
        {
            Id = 53,
            PONumber = "PO-53",
            SupplierId = 7,
            Status = PurchaseOrderStatus.Approved,
            CommercialVersion = 3,
            ApprovedCommercialVersion = 3,
            ApprovedCommercialSnapshotJson = "{\"schemaVersion\":1}",
            CurrencyScale = 2
        };
        var line = new OrderDetail
        {
            Id = 9,
            PurchaseOrderId = po.Id,
            ItemId = 21,
            Quantity = 2,
            UnitPrice = 4m,
            CurrencyScale = 2
        };
        var stableLineId = line.DocumentLineId;
        _poRepoMock.Setup(repository => repository.GetByIdAsync(po.Id)).ReturnsAsync(po);
        SetupSnapshotData(po, line);

        await _sut.AmendApprovedAsync(po.Id, new Merconiq.Core.Models.PurchaseOrderAmendment(
            ExpectedCommercialVersion: 3,
            SupplierId: 7,
            DeliveryTerms: "Deliver to receiving bay",
            Notes: null,
            CurrencyScale: 2,
            Lines: [new Merconiq.Core.Models.PurchaseOrderAmendmentLine(
                line.Id, line.ItemId, line.Quantity, 6m, line.DiscountPercent,
                line.TaxRuleId, line.TaxRatePercent, line.TaxCategory, line.TaxMode, line.Direction)]));

        po.Status.Should().Be(PurchaseOrderStatus.Pending);
        po.CommercialVersion.Should().Be(4);
        po.ApprovedCommercialVersion.Should().Be(3);
        po.ApprovedCommercialSnapshotJson.Should().Be("{\"schemaVersion\":1}");
        po.TotalAmount.Should().Be(12m);
        line.UnitPrice.Should().Be(6m);
        line.DocumentLineId.Should().Be(stableLineId);
        _uowMock.Verify(unitOfWork => unitOfWork.SaveChangesAsync(default), Times.Once);
    }

    [Fact]
    public async Task AmendApprovedAsync_WhenOrderIsReceived_RejectsWithoutChangingCommercialOrLineIdentity()
    {
        var originalSnapshot = "{\"schemaVersion\":1,\"approved\":true}";
        var po = new PurchaseOrder
        {
            Id = 57,
            PONumber = "PO-57",
            SupplierId = 7,
            Status = PurchaseOrderStatus.Received,
            CommercialVersion = 3,
            ApprovedCommercialVersion = 3,
            ApprovedCommercialSnapshotJson = originalSnapshot,
            CurrencyScale = 2,
            DeliveryTerms = "Deliver to Dock 1"
        };
        var line = new OrderDetail
        {
            Id = 12,
            PurchaseOrderId = po.Id,
            ItemId = 21,
            Quantity = 4,
            UnitPrice = 4m,
            CurrencyScale = 2
        };
        var stableLineId = line.DocumentLineId;
        var originalVersion = po.Version;
        _poRepoMock.Setup(repository => repository.GetByIdAsync(po.Id)).ReturnsAsync(po);
        SetupSnapshotData(po, line);

        var act = () => _sut.AmendApprovedAsync(po.Id, new PurchaseOrderAmendment(
            ExpectedCommercialVersion: 3,
            SupplierId: po.SupplierId,
            DeliveryTerms: "Deliver to Dock 2",
            Notes: null,
            CurrencyScale: 2,
            Lines: [new PurchaseOrderAmendmentLine(
                line.Id, line.ItemId, 5, 6m, line.DiscountPercent,
                line.TaxRuleId, line.TaxRatePercent, line.TaxCategory, line.TaxMode, line.Direction)]));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Only an approved purchase order can be amended.");

        po.Status.Should().Be(PurchaseOrderStatus.Received);
        po.Version.Should().Be(originalVersion);
        po.CommercialVersion.Should().Be(3);
        po.ApprovedCommercialVersion.Should().Be(3);
        po.ApprovedCommercialSnapshotJson.Should().Be(originalSnapshot);
        po.SupplierId.Should().Be(7);
        po.DeliveryTerms.Should().Be("Deliver to Dock 1");
        line.Id.Should().Be(12);
        line.DocumentLineId.Should().Be(stableLineId);
        line.PurchaseOrderId.Should().Be(po.Id);
        line.ItemId.Should().Be(21);
        line.Quantity.Should().Be(4);
        line.UnitPrice.Should().Be(4m);
        _uowMock.Verify(unitOfWork => unitOfWork.SaveChangesAsync(default), Times.Never);
    }

    [Fact]
    public async Task AmendApprovedAsync_PreservesHistoricalTaxSnapshotWhenSelectedRuleIsNoLongerActive()
    {
        var effectiveFrom = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var po = new PurchaseOrder
        {
            Id = 56,
            PONumber = "PO-56",
            SupplierId = 7,
            Status = PurchaseOrderStatus.Approved,
            CommercialVersion = 1,
            ApprovedCommercialVersion = 1,
            ApprovedCommercialSnapshotJson = "{\"schemaVersion\":1}",
            CurrencyScale = 2
        };
        var line = new OrderDetail
        {
            Id = 11,
            PurchaseOrderId = po.Id,
            ItemId = 21,
            Quantity = 2,
            UnitPrice = 4m,
            CurrencyScale = 2,
            TaxRuleId = 77,
            TaxRatePercent = 7.5m,
            TaxCategory = TaxCategory.Standard,
            TaxMode = TaxCalculationMode.Inclusive,
            TaxEffectiveFromUtc = effectiveFrom
        };
        _poRepoMock.Setup(repository => repository.GetByIdAsync(po.Id)).ReturnsAsync(po);
        SetupSnapshotData(po, line);

        await _sut.AmendApprovedAsync(po.Id, new Merconiq.Core.Models.PurchaseOrderAmendment(
            ExpectedCommercialVersion: 1,
            SupplierId: po.SupplierId,
            DeliveryTerms: "Updated receiving bay",
            Notes: null,
            CurrencyScale: 2,
            Lines: [new Merconiq.Core.Models.PurchaseOrderAmendmentLine(
                line.Id, line.ItemId, line.Quantity, line.UnitPrice, line.DiscountPercent,
                line.TaxRuleId, line.TaxRatePercent, line.TaxCategory, line.TaxMode, line.Direction)]));

        line.TaxRatePercent.Should().Be(7.5m);
        line.TaxCategory.Should().Be(TaxCategory.Standard);
        line.TaxMode.Should().Be(TaxCalculationMode.Inclusive);
        line.TaxEffectiveFromUtc.Should().Be(effectiveFrom);
        _taxRuleRepoMock.Verify(repository => repository.FindAsync(
            It.IsAny<Expression<Func<TaxRule, bool>>>()), Times.Never);
    }

    [Fact]
    public async Task AmendApprovedAsync_WhenCommercialTermsAreUnchanged_DoesNotInvalidateApproval()
    {
        var po = new PurchaseOrder
        {
            Id = 54,
            PONumber = "PO-54",
            SupplierId = 7,
            Status = PurchaseOrderStatus.Approved,
            CommercialVersion = 1,
            ApprovedCommercialVersion = 1,
            ApprovedCommercialSnapshotJson = "{\"schemaVersion\":1}",
            CurrencyScale = 2,
            DeliveryTerms = "Collect"
        };
        var line = new OrderDetail
        {
            Id = 10,
            PurchaseOrderId = po.Id,
            ItemId = 21,
            Quantity = 2,
            UnitPrice = 4m,
            CurrencyScale = 2
        };
        _poRepoMock.Setup(repository => repository.GetByIdAsync(po.Id)).ReturnsAsync(po);
        SetupSnapshotData(po, line);

        await _sut.AmendApprovedAsync(po.Id, new Merconiq.Core.Models.PurchaseOrderAmendment(
            ExpectedCommercialVersion: 1,
            SupplierId: 7,
            DeliveryTerms: "Collect",
            Notes: null,
            CurrencyScale: 2,
            Lines: [new Merconiq.Core.Models.PurchaseOrderAmendmentLine(
                line.Id, line.ItemId, line.Quantity, line.UnitPrice, line.DiscountPercent,
                line.TaxRuleId, line.TaxRatePercent, line.TaxCategory, line.TaxMode, line.Direction)]));

        po.Status.Should().Be(PurchaseOrderStatus.Approved);
        po.CommercialVersion.Should().Be(1);
        po.ApprovedCommercialVersion.Should().Be(1);
        _uowMock.Verify(unitOfWork => unitOfWork.SaveChangesAsync(default), Times.Never);
    }

    [Fact]
    public async Task AmendApprovedAsync_WhenExpectedVersionIsStale_RejectsWithoutMutation()
    {
        var po = new PurchaseOrder
        {
            Id = 55,
            SupplierId = 7,
            Status = PurchaseOrderStatus.Approved,
            CommercialVersion = 2,
            ApprovedCommercialVersion = 2,
            ApprovedCommercialSnapshotJson = "{\"schemaVersion\":1}"
        };
        _poRepoMock.Setup(repository => repository.GetByIdAsync(po.Id)).ReturnsAsync(po);

        var act = () => _sut.AmendApprovedAsync(po.Id, new Merconiq.Core.Models.PurchaseOrderAmendment(
            1, 7, null, null, 2, []));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The purchase order changed after this amendment form was opened. Reload and try again.");
        po.Status.Should().Be(PurchaseOrderStatus.Approved);
        po.CommercialVersion.Should().Be(2);
        _uowMock.Verify(unitOfWork => unitOfWork.SaveChangesAsync(default), Times.Never);
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

        await _sut.UpdateStatusAsync(po.Id, nameof(PurchaseOrderStatus.Voided), TestActor);

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

        var act = () => _sut.UpdateStatusAsync(po.Id, nameof(PurchaseOrderStatus.Voided), TestActor);

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

    private void SetupSnapshotData(PurchaseOrder po, params OrderDetail[] lines)
    {
        if (string.IsNullOrWhiteSpace(po.PONumber) || po.PONumber.Length > 50)
            po.PONumber = $"PO-{po.Id}";
        if (po.OrderDate.Year is < 2000 or > 9999)
            po.OrderDate = DateTime.UtcNow;

        _testLines = lines.ToList();
        foreach (var line in lines)
            _orderDetailRepoMock.Setup(repository => repository.GetByIdAsync(line.Id)).ReturnsAsync(line);
        _testSuppliers = [new Supplier { Id = po.SupplierId, Name = "Approved supplier", Address = "1 Warehouse Road", Email = "buyer@example.test" }];
        _testItems = lines.Select(line => new Item
        {
            Id = line.ItemId,
            ItemCode = $"ITEM-{line.ItemId}",
            Description = "Approved item"
        }).DistinctBy(item => item.Id).ToList();
        _testUnits = [];
        _testDocuments = [DocumentIdentity.Create(
            po.DocumentId,
            po.TenantId,
            companyId: null,
            "PurchaseOrder",
            po.PONumber,
            po.OrderDate.Year,
            DocumentLifecycleStatus.Active,
            "PurchaseOrderServiceTests")];
    }
}
