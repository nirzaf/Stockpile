using System.Linq.Expressions;
using AutoFixture;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Exceptions;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Core.Services;
using Merconiq.Tests.Common;
using Merconiq.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Merconiq.Tests.Core.Services;

public class StockServiceTests
{
    private readonly Fixture _fixture = InventoryFixtureFactory.Create();
    private readonly Mock<IRepository<StockInHand>> _stockRepoMock = new();
    private readonly Mock<IRepository<StockTransaction>> _txRepoMock = new();
    private readonly Mock<IRepository<Item>> _itemRepoMock = new();
    private readonly Mock<IRepository<Location>> _locationRepoMock = new();
    private readonly Mock<IRepository<Branch>> _branchRepoMock = new();
    private readonly Mock<IRepository<StockValuationBucket>> _valuationBucketRepoMock = new();
    private readonly Mock<IRepository<StockValuationEntry>> _valuationEntryRepoMock = new();
    private readonly Mock<IUnitOfWork> _uowMock = new();
    private readonly Mock<IWebhookDispatcher> _webhookDispatcherMock = new();
    private readonly List<Location> _locations = Enumerable.Range(1, 100)
        .Select(id => new Location { Id = id, TenantId = "test-tenant" })
        .ToList();
    private readonly List<Branch> _branches = new();
    private readonly StockService _sut;

    public StockServiceTests()
    {
        _itemRepoMock.Setup(r => r.GetByIdAsync(It.IsAny<int>()))
            .ReturnsAsync((Item?)null);
        _locationRepoMock.Setup(r => r.FindAsync(It.IsAny<Expression<Func<Location, bool>>>() ))
            .Returns((Expression<Func<Location, bool>> predicate) =>
                Task.FromResult<IEnumerable<Location>>(_locations
                    .Where(location => !location.IsDeleted && location.TenantId == "test-tenant")
                    .Where(predicate.Compile())
                    .ToArray()));
        _branchRepoMock.Setup(r => r.FindAsync(It.IsAny<Expression<Func<Branch, bool>>>() ))
            .Returns((Expression<Func<Branch, bool>> predicate) =>
                Task.FromResult<IEnumerable<Branch>>(_branches
                    .Where(branch => branch.TenantId == "test-tenant")
                    .Where(predicate.Compile())
                    .ToArray()));
        _valuationBucketRepoMock
            .Setup(r => r.FindAsync(It.IsAny<Expression<Func<StockValuationBucket, bool>>>() ))
            .ReturnsAsync(Array.Empty<StockValuationBucket>());
        _uowMock
            .Setup(u => u.ExecuteInTransactionAsync(
                It.IsAny<Func<Task>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Func<Task<bool>>?>()))
            .Returns((Func<Task> operation, CancellationToken _, Func<Task<bool>>? _) => operation());
        _sut = new StockService(
            _stockRepoMock.Object,
            _txRepoMock.Object,
            _itemRepoMock.Object,
            _locationRepoMock.Object,
            _branchRepoMock.Object,
            _uowMock.Object,
            _webhookDispatcherMock.Object,
            new TestTenantContext("test-tenant"),
            NullLogger<StockService>.Instance,
            _valuationBucketRepoMock.Object,
            _valuationEntryRepoMock.Object);
    }

    [Fact]
    public void Constructor_RejectsMissingValuationRepositories()
    {
        var act = () => new StockService(
            _stockRepoMock.Object,
            _txRepoMock.Object,
            _itemRepoMock.Object,
            _locationRepoMock.Object,
            _branchRepoMock.Object,
            _uowMock.Object,
            _webhookDispatcherMock.Object,
            new TestTenantContext("test-tenant"),
            NullLogger<StockService>.Instance,
            null!,
            _valuationEntryRepoMock.Object);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("valuationBucketRepo");
    }

    // Helper: setup FindAsync single-parameter overload
    private void SetupStockFindAsync(List<StockInHand> result)
    {
        _stockRepoMock.Setup(r => r.FindAsync(
                It.IsAny<Expression<Func<StockInHand, bool>>>()))
            .ReturnsAsync(result);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsAllStock()
    {
        var stock = _fixture.CreateMany<StockInHand>(3).ToList();
        _stockRepoMock.Setup(r => r.GetAllAsync()).ReturnsAsync(stock);

        var result = await _sut.GetAllAsync();

        result.Should().BeEquivalentTo(stock);
    }

    [Fact]
    public async Task GetByItemAndLocationAsync_WhenExists_ReturnsStock()
    {
        var stock = _fixture.Create<StockInHand>();
        stock.ItemId = 1;
        stock.LocationId = 2;
        SetupStockFindAsync(new List<StockInHand> { stock });

        var result = await _sut.GetByItemAndLocationAsync(1, 2);

        result.Should().NotBeNull();
        result!.ItemId.Should().Be(1);
        result.LocationId.Should().Be(2);
    }

    [Fact]
    public async Task GetByItemAndLocationAsync_WhenNotExists_ReturnsNull()
    {
        SetupStockFindAsync(new List<StockInHand>());

        var result = await _sut.GetByItemAndLocationAsync(1, 2);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByItemAndLocationAsync_distinguishes_same_batch_by_expiry_date()
    {
        var earlierExpiry = new DateTime(2027, 1, 1);
        var laterExpiry = new DateTime(2028, 1, 1);
        var stock = new[]
        {
            new StockInHand { ItemId = 1, LocationId = 2, BatchNumber = "LOT-001", ExpiryDate = earlierExpiry, Quantity = 10 },
            new StockInHand { ItemId = 1, LocationId = 2, BatchNumber = "LOT-001", ExpiryDate = laterExpiry, Quantity = 20 }
        };
        _stockRepoMock.Setup(repository => repository.FindAsync(
                It.IsAny<Expression<Func<StockInHand, bool>>>()))
            .Returns((Expression<Func<StockInHand, bool>> predicate) =>
                Task.FromResult<IEnumerable<StockInHand>>(stock.Where(predicate.Compile()).ToArray()));

        var result = await _sut.GetByItemAndLocationAsync(1, 2, "LOT-001", laterExpiry);

        result.Should().NotBeNull();
        result!.Quantity.Should().Be(20);
        result.ExpiryDate.Should().Be(laterExpiry);
    }

    [Fact]
    public async Task GetTransactionsAsync_ReturnsFilteredTransactions()
    {
        var txs = _fixture.CreateMany<StockTransaction>(5).ToList();
        _txRepoMock.Setup(r => r.FindAsync(
                It.IsAny<Expression<Func<StockTransaction, bool>>>(),
                It.IsAny<Func<IQueryable<StockTransaction>, IOrderedQueryable<StockTransaction>>>()))
            .ReturnsAsync(txs);

        var result = await _sut.GetTransactionsAsync(null, null);

        result.Should().BeEquivalentTo(txs);
    }

    [Fact]
    public async Task GetTransactionsForCompanies_IncludesLocationMovementsAndOnlySameCompanyTransfers()
    {
        StockTransaction CreateTransaction(int id, TransactionType type, int fromCompanyId, int? toCompanyId)
        {
            return new StockTransaction
            {
                Id = id,
                TransactionType = type,
                TransactionDate = DateTime.UtcNow.AddMinutes(-id),
                FromLocation = new Location
                {
                    Id = id * 10,
                    Branch = new Branch { CompanyId = fromCompanyId }
                },
                ToLocationId = toCompanyId.HasValue ? id * 10 + 1 : null,
                ToLocation = toCompanyId.HasValue
                    ? new Location
                    {
                        Id = id * 10 + 1,
                        Branch = new Branch { CompanyId = toCompanyId.Value }
                    }
                    : null
            };
        }

        var transactions = new[]
        {
            CreateTransaction(1, TransactionType.Receive, 101, null),
            CreateTransaction(2, TransactionType.Sell, 101, null),
            CreateTransaction(3, TransactionType.Transfer, 101, 202),
            CreateTransaction(4, TransactionType.Transfer, 101, 101)
        };
        _txRepoMock.Setup(repository => repository.FindAsync(
                It.IsAny<Expression<Func<StockTransaction, bool>>>(),
                It.IsAny<Func<IQueryable<StockTransaction>, IOrderedQueryable<StockTransaction>>>() ))
            .Returns((Expression<Func<StockTransaction, bool>> predicate,
                Func<IQueryable<StockTransaction>, IOrderedQueryable<StockTransaction>> orderBy) =>
            {
                var rows = orderBy(transactions.AsQueryable().Where(predicate)).ToArray();
                return Task.FromResult<IEnumerable<StockTransaction>>(rows);
            });

        var result = await _sut.GetTransactionsForCompaniesAsync(null, null, [101]);

        result.Select(transaction => transaction.Id).Should().BeEquivalentTo(new[] { 1, 2, 4 });
    }

    [Fact]
    public async Task ReceiveStockAsync_ExistingStock_IncrementsQuantity()
    {
        var existing = _fixture.Create<StockInHand>();
        existing.ItemId = 1;
        existing.LocationId = 2;
        existing.Quantity = 50;
        SetupStockFindAsync(new List<StockInHand> { existing });

        await _sut.ReceiveStockAsync(1, 2, 25, null);

        existing.Quantity.Should().Be(75);
        _stockRepoMock.Verify(r => r.UpdateAsync(existing), Times.Once);
        _txRepoMock.Verify(r => r.AddAsync(It.Is<StockTransaction>(
            t => t.TransactionType == TransactionType.Receive && t.Quantity == 25)), Times.Once);
        _uowMock.Verify(u => u.SaveChangesAsync(default), Times.Once);
    }

    [Fact]
    public async Task ReceiveStockAsync_NewStock_CreatesStockInHand()
    {
        SetupStockFindAsync(new List<StockInHand>());

        await _sut.ReceiveStockAsync(1, 2, 30, "New shipment");

        _stockRepoMock.Verify(r => r.AddAsync(It.Is<StockInHand>(
            s => s.ItemId == 1 && s.LocationId == 2 && s.Quantity == 30)), Times.Once);
        _txRepoMock.Verify(r => r.AddAsync(It.IsAny<StockTransaction>()), Times.Once);
        _uowMock.Verify(u => u.SaveChangesAsync(default), Times.Once);
    }

    [Fact]
    public async Task ReceiveStockAsync_NewBatch_PersistsBatchMetadataOnStockInHand()
    {
        SetupStockFindAsync(new List<StockInHand>());
        var expiryDate = DateTime.UtcNow.AddMonths(6);

        await _sut.ReceiveStockAsync(1, 2, 30, "New batch", "LOT-001", expiryDate);

        _stockRepoMock.Verify(r => r.AddAsync(It.Is<StockInHand>(stock =>
            stock.ItemId == 1 &&
            stock.LocationId == 2 &&
            stock.Quantity == 30 &&
            stock.BatchNumber == "LOT-001" &&
            stock.ExpiryDate == StockLotExpiryDate.Normalize(expiryDate))), Times.Once);
    }

    [Fact]
    public async Task ReceiveStockAsync_NonPositiveQuantity_Throws()
    {
        var act = () => _sut.ReceiveStockAsync(1, 2, 0, null);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("Quantity must be positive");
    }

    [Fact]
    public async Task ReceiveStockAsync_UnknownLocation_ThrowsBeforeWriting()
    {
        _locations.RemoveAll(location => location.Id == 2);

        var act = () => _sut.ReceiveStockAsync(1, 2, 1, null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Location does not exist in the current tenant or is deleted.");
        _stockRepoMock.Verify(r => r.AddAsync(It.IsAny<StockInHand>()), Times.Never);
        _txRepoMock.Verify(r => r.AddAsync(It.IsAny<StockTransaction>()), Times.Never);
    }

    [Fact]
    public async Task ReceiveStockAsync_DeletedLocation_IsRejectedBeforeWriting()
    {
        _locations.Single(location => location.Id == 2).IsDeleted = true;

        var act = () => _sut.ReceiveStockAsync(1, 2, 1, null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Location does not exist in the current tenant or is deleted.");
        _stockRepoMock.Verify(repository => repository.FindAsync(
            It.IsAny<Expression<Func<StockInHand, bool>>>()), Times.Never);
        _txRepoMock.Verify(repository => repository.AddAsync(It.IsAny<StockTransaction>()), Times.Never);
    }

    [Fact]
    public async Task ReceiveStockAsync_LocationReturnedFromAnotherTenant_IsRejected()
    {
        _locationRepoMock.Setup(repository => repository.FindAsync(
                It.IsAny<Expression<Func<Location, bool>>>() ))
            .Returns((Expression<Func<Location, bool>> _) =>
                Task.FromResult<IEnumerable<Location>>(new[]
                {
                    new Location { Id = 2, TenantId = "another-tenant" }
                }));

        var act = () => _sut.ReceiveStockAsync(1, 2, 1, null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Location does not exist in the current tenant or is deleted.");
    }

    [Fact]
    public async Task ReceiveStockAsync_ConcurrencyConflict_ReloadsAndRetries()
    {
        var firstRead = new StockInHand { ItemId = 1, LocationId = 2, Quantity = 50 };
        var refreshedRead = new StockInHand { ItemId = 1, LocationId = 2, Quantity = 50 };
        _stockRepoMock.SetupSequence(r => r.FindAsync(
                It.IsAny<Expression<Func<StockInHand, bool>>>()))
            .ReturnsAsync(new List<StockInHand> { firstRead })
            .ReturnsAsync(new List<StockInHand> { refreshedRead });
        _uowMock.SetupSequence(u => u.SaveChangesAsync(default))
            .ThrowsAsync(new ConcurrencyException("simulated conflict"))
            .ReturnsAsync(1);

        await _sut.ReceiveStockAsync(1, 2, 25, null);

        refreshedRead.Quantity.Should().Be(75);
        _uowMock.Verify(u => u.ClearTracker(), Times.Once);
        _uowMock.Verify(u => u.SaveChangesAsync(default), Times.Exactly(2));
    }

    [Fact]
    public async Task TransferStockAsync_Success_UpdatesBothLocations()
    {
        _locations.Single(location => location.Id == 10).BranchId = 301;
        _locations.Single(location => location.Id == 20).BranchId = 301;
        _branches.Add(new Branch { Id = 301, CompanyId = 401, TenantId = "test-tenant" });
        var source = _fixture.Create<StockInHand>();
        source.ItemId = 1;
        source.LocationId = 10;
        source.Quantity = 100;
        source.ReservedQuantity = 0;

        var dest = _fixture.Create<StockInHand>();
        dest.ItemId = 1;
        dest.LocationId = 20;
        dest.Quantity = 50;
        dest.ReservedQuantity = 0;

        // Setup sequential calls: first call returns source, second returns dest
        _stockRepoMock.SetupSequence(r => r.FindAsync(
                It.IsAny<Expression<Func<StockInHand, bool>>>()))
            .ReturnsAsync(new List<StockInHand> { source })
            .ReturnsAsync(new List<StockInHand> { dest });

        await _sut.TransferStockAsync(1, 10, 20, 30, "Transfer notes");

        source.Quantity.Should().Be(70);
        dest.Quantity.Should().Be(80);
        _stockRepoMock.Verify(r => r.UpdateAsync(source), Times.Once);
        _stockRepoMock.Verify(r => r.UpdateAsync(dest), Times.Once);
        _txRepoMock.Verify(r => r.AddAsync(It.Is<StockTransaction>(
            t => t.TransactionType == TransactionType.Transfer && t.Quantity == 30 &&
                 t.FromLocationId == 10 && t.ToLocationId == 20)), Times.Once);
        _uowMock.Verify(u => u.SaveChangesAsync(default), Times.Once);
    }

    [Fact]
    public async Task TransferStockAsync_InsufficientStock_Throws()
    {
        _locations.Single(location => location.Id == 10).BranchId = 301;
        _locations.Single(location => location.Id == 20).BranchId = 301;
        _branches.Add(new Branch { Id = 301, CompanyId = 401, TenantId = "test-tenant" });
        var source = _fixture.Create<StockInHand>();
        source.ItemId = 1;
        source.LocationId = 10;
        source.Quantity = 5;
        SetupStockFindAsync(new List<StockInHand> { source });

        var act = () => _sut.TransferStockAsync(1, 10, 20, 50, null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Insufficient stock at source location");
    }

    [Fact]
    public async Task TransferStockAsync_ReservedQuantity_BlocksTransfer()
    {
        _locations.Single(location => location.Id == 10).BranchId = 301;
        _locations.Single(location => location.Id == 20).BranchId = 301;
        _branches.Add(new Branch { Id = 301, CompanyId = 401, TenantId = "test-tenant" });
        var source = new StockInHand { ItemId = 1, LocationId = 10, Quantity = 10, ReservedQuantity = 6 };
        _stockRepoMock.Setup(r => r.FindAsync(It.IsAny<Expression<Func<StockInHand, bool>>>() ))
            .ReturnsAsync([source]);

        var action = () => _sut.TransferStockAsync(1, 10, 20, 5, null);

        await action.Should().ThrowAsync<StockAvailabilityConflictException>();
        source.Quantity.Should().Be(10);
        _stockRepoMock.Verify(r => r.UpdateAsync(It.IsAny<StockInHand>()), Times.Never);
    }

    [Fact]
    public async Task TransferStockAsync_SameLocation_Throws()
    {
        var act = () => _sut.TransferStockAsync(1, 10, 10, 10, null);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("Source and destination must be different");
    }

    [Fact]
    public async Task TransferStockAsync_CrossCompanyLocations_ThrowsBeforeWriting()
    {
        _locations.AddRange(new[]
        {
            new Location { Id = 201, BranchId = 301, TenantId = "test-tenant" },
            new Location { Id = 202, BranchId = 302, TenantId = "test-tenant" }
        });
        _branches.AddRange(new[]
        {
            new Branch { Id = 301, CompanyId = 401, TenantId = "test-tenant" },
            new Branch { Id = 302, CompanyId = 402, TenantId = "test-tenant" }
        });

        var act = () => _sut.TransferStockAsync(1, 201, 202, 1, null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Cross-company stock transfers are not supported.");
        _stockRepoMock.Verify(r => r.FindAsync(
            It.IsAny<Expression<Func<StockInHand, bool>>>()), Times.Never);
    }

    [Fact]
    public async Task TransferStockAsync_MappedLocationsInSameCompany_AreAllowed()
    {
        _locations.AddRange(new[]
        {
            new Location { Id = 201, BranchId = 301, TenantId = "test-tenant" },
            new Location { Id = 202, BranchId = 302, TenantId = "test-tenant" }
        });
        _branches.AddRange(new[]
        {
            new Branch { Id = 301, CompanyId = 401, TenantId = "test-tenant" },
            new Branch { Id = 302, CompanyId = 401, TenantId = "test-tenant" }
        });
        var source = new StockInHand { ItemId = 1, LocationId = 201, Quantity = 5 };
        _stockRepoMock.Setup(repository => repository.FindAsync(
                It.IsAny<Expression<Func<StockInHand, bool>>>() ))
            .Returns((Expression<Func<StockInHand, bool>> predicate) =>
                Task.FromResult<IEnumerable<StockInHand>>(new[] { source }.Where(predicate.Compile()).ToArray()));

        await _sut.TransferStockAsync(1, 201, 202, 2, null);

        source.Quantity.Should().Be(3);
        _stockRepoMock.Verify(repository => repository.AddAsync(It.Is<StockInHand>(stock =>
            stock.LocationId == 202 && stock.Quantity == 2)), Times.Once);
    }

    [Fact]
    public async Task TransferStockAsync_MappedToUnmappedLegacyLocation_IsRejected()
    {
        _locations.Add(new Location { Id = 201, BranchId = 301, TenantId = "test-tenant" });
        _branches.Add(new Branch { Id = 301, CompanyId = 401, TenantId = "test-tenant" });
        var source = new StockInHand { ItemId = 1, LocationId = 201, Quantity = 5 };
        _stockRepoMock.Setup(repository => repository.FindAsync(
                It.IsAny<Expression<Func<StockInHand, bool>>>() ))
            .Returns((Expression<Func<StockInHand, bool>> predicate) =>
                Task.FromResult<IEnumerable<StockInHand>>(new[] { source }.Where(predicate.Compile()).ToArray()));

        var act = () => _sut.TransferStockAsync(1, 201, 20, 2, null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Both locations must be assigned to an active company before stock can be transferred.");
        source.Quantity.Should().Be(5);
        _stockRepoMock.Verify(repository => repository.AddAsync(It.IsAny<StockInHand>()), Times.Never);
    }

    [Fact]
    public async Task SellStockAsync_Success_DecrementsQuantity()
    {
        var stock = _fixture.Create<StockInHand>();
        stock.ItemId = 1;
        stock.LocationId = 2;
        stock.Quantity = 100;
        stock.ReservedQuantity = 0;
        SetupStockFindAsync(new List<StockInHand> { stock });

        await _sut.SellStockAsync(1, 2, 30, "Sold to customer");

        stock.Quantity.Should().Be(70);
        _stockRepoMock.Verify(r => r.UpdateAsync(stock), Times.Once);
        _txRepoMock.Verify(r => r.AddAsync(It.Is<StockTransaction>(
            t => t.TransactionType == TransactionType.Sell && t.Quantity == 30)), Times.Once);
        _uowMock.Verify(u => u.SaveChangesAsync(default), Times.Once);
    }

    [Fact]
    public async Task SellStockAsync_InsufficientStock_Throws()
    {
        var stock = _fixture.Create<StockInHand>();
        stock.ItemId = 1;
        stock.LocationId = 2;
        stock.Quantity = 5;
        SetupStockFindAsync(new List<StockInHand> { stock });

        var act = () => _sut.SellStockAsync(1, 2, 50, null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Insufficient stock for sale");
    }

    [Fact]
    public async Task SellStockAsync_WhenBalanceReachesReorderLevel_QueuesLowStockNotification()
    {
        var item = _fixture.Build<Item>()
            .With(i => i.Id, 1)
            .With(i => i.ItemCode, "LOW-001")
            .With(i => i.ReorderLevel, 10)
            .With(i => i.IsActive, true)
            .Create();
        var stock = _fixture.Build<StockInHand>()
            .With(s => s.ItemId, 1)
            .With(s => s.LocationId, 2)
            .With(s => s.Quantity, 15)
            .With(s => s.ReservedQuantity, 0)
            .Create();
        _itemRepoMock.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(item);
        _stockRepoMock.SetupSequence(r => r.FindAsync(
                It.IsAny<Expression<Func<StockInHand, bool>>>()))
            .ReturnsAsync(new List<StockInHand> { stock })
            .ReturnsAsync(new List<StockInHand> { stock });

        await _sut.SellStockAsync(1, 2, 5, "Reorder threshold reached");

        var invocation = _webhookDispatcherMock.Invocations
            .Single(i => i.Method.Name == nameof(IWebhookDispatcher.EnqueueAsync) &&
                         i.Arguments[0]!.GetType().GetProperty("EventType")!.GetValue(i.Arguments[0]) is "Stock.Low");
        var webhookEvent = invocation.Arguments[0]!;
        webhookEvent.GetType().GetProperty("TenantId")!.GetValue(webhookEvent).Should().Be("test-tenant");
        var payload = webhookEvent.GetType().GetProperty("Payload")!.GetValue(webhookEvent);
        payload!.GetType().GetProperty("ItemId")!.GetValue(payload).Should().Be(1);
        payload.GetType().GetProperty("ItemCode")!.GetValue(payload).Should().Be("LOW-001");
        payload.GetType().GetProperty("TotalStock")!.GetValue(payload).Should().Be(10);
        payload.GetType().GetProperty("ReorderLevel")!.GetValue(payload).Should().Be(10);
        _uowMock.Verify(u => u.ExecuteInTransactionAsync(
            It.IsAny<Func<Task>>(), CancellationToken.None, It.IsAny<Func<Task<bool>>?>()), Times.Once);
    }

    [Fact]
    public async Task StockOperation_rejects_inactive_items()
    {
        _itemRepoMock.Setup(r => r.GetByIdAsync(1))
            .ReturnsAsync(new Item { Id = 1, IsActive = false });

        var act = () => _sut.SellStockAsync(1, 2, 1, null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Inactive items cannot be used in stock operations.");
    }

    [Fact]
    public async Task ReceiveStockAsync_rejects_ownership_changed_after_authorization()
    {
        _locations[0].BranchId = 7;
        _branches.Add(new Branch { Id = 7, TenantId = "test-tenant", CompanyId = 22, IsActive = true });

        var act = () => _sut.ReceiveStockAsync(
            1, _locations[0].Id, 1, "authorized for another company",
            mutationScope: new StockMutationScope(11));

        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("Location ownership changed while stock access was being authorized.*");
        _uowMock.Verify(u => u.AcquireLocationLocksAsync(
            It.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { _locations[0].Id })),
            It.IsAny<CancellationToken>()), Times.Once);
        _stockRepoMock.Verify(r => r.AddAsync(It.IsAny<StockInHand>()), Times.Never);
        _txRepoMock.Verify(r => r.AddAsync(It.IsAny<StockTransaction>()), Times.Never);
    }

    [Fact]
    public async Task ReleaseReservationAsync_acquires_its_location_lock_before_mutating()
    {
        var reservation = new StockReservation
        {
            Id = 19,
            ItemId = 1,
            LocationId = 4,
            SourceLineReference = "line-19",
            Quantity = 3,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        var stock = new StockInHand
        {
            ItemId = 1,
            LocationId = 4,
            Quantity = 8,
            ReservedQuantity = 3
        };
        var reservationRepo = new Mock<IRepository<StockReservation>>();
        reservationRepo.Setup(repo => repo.FindAsync(It.IsAny<Expression<Func<StockReservation, bool>>>() ))
            .Returns((Expression<Func<StockReservation, bool>> predicate) =>
                Task.FromResult<IEnumerable<StockReservation>>(
                    new[] { reservation }.Where(predicate.Compile()).ToArray()));
        _stockRepoMock.Setup(repo => repo.FindAsync(It.IsAny<Expression<Func<StockInHand, bool>>>() ))
            .Returns((Expression<Func<StockInHand, bool>> predicate) =>
                Task.FromResult<IEnumerable<StockInHand>>(
                    new[] { stock }.Where(predicate.Compile()).ToArray()));
        var service = new StockService(
            _stockRepoMock.Object,
            _txRepoMock.Object,
            _itemRepoMock.Object,
            _locationRepoMock.Object,
            _branchRepoMock.Object,
            _uowMock.Object,
            _webhookDispatcherMock.Object,
            new TestTenantContext("test-tenant"),
            NullLogger<StockService>.Instance,
            _valuationBucketRepoMock.Object,
            _valuationEntryRepoMock.Object,
            reservationRepo.Object);

        await service.ReleaseReservationAsync(
            "line-19", "release test", new StockMutationScope(null));

        _uowMock.Verify(unit => unit.AcquireLocationLocksAsync(
            It.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 4 })),
            It.IsAny<CancellationToken>()), Times.Once);
        stock.ReservedQuantity.Should().Be(0);
        reservation.Status.Should().Be(StockReservationStatus.Released);
    }
}
