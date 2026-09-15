using System.Linq.Expressions;
using AutoFixture;
using FluentAssertions;
using InventoryManagementSystem.Core.Entities;
using InventoryManagementSystem.Core.Exceptions;
using InventoryManagementSystem.Core.Interfaces;
using InventoryManagementSystem.Core.Services;
using InventoryManagementSystem.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace InventoryManagementSystem.Tests.Core.Services;

public class StockServiceTests
{
    private readonly Fixture _fixture = InventoryFixtureFactory.Create();
    private readonly Mock<IRepository<StockInHand>> _stockRepoMock = new();
    private readonly Mock<IRepository<StockTransaction>> _txRepoMock = new();
    private readonly Mock<IRepository<Item>> _itemRepoMock = new();
    private readonly Mock<IUnitOfWork> _uowMock = new();
    private readonly Mock<IWebhookDispatcher> _webhookDispatcherMock = new();
    private readonly StockService _sut;

    public StockServiceTests()
    {
        _sut = new StockService(
            _stockRepoMock.Object,
            _txRepoMock.Object,
            _itemRepoMock.Object,
            _uowMock.Object,
            _webhookDispatcherMock.Object,
            NullLogger<StockService>.Instance);
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
    public async Task ReceiveStockAsync_NonPositiveQuantity_Throws()
    {
        var act = () => _sut.ReceiveStockAsync(1, 2, 0, null);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("Quantity must be positive");
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
        var source = _fixture.Create<StockInHand>();
        source.ItemId = 1;
        source.LocationId = 10;
        source.Quantity = 100;

        var dest = _fixture.Create<StockInHand>();
        dest.ItemId = 1;
        dest.LocationId = 20;
        dest.Quantity = 50;

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
    public async Task TransferStockAsync_SameLocation_Throws()
    {
        var act = () => _sut.TransferStockAsync(1, 10, 10, 10, null);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("Source and destination must be different");
    }

    [Fact]
    public async Task SellStockAsync_Success_DecrementsQuantity()
    {
        var stock = _fixture.Create<StockInHand>();
        stock.ItemId = 1;
        stock.LocationId = 2;
        stock.Quantity = 100;
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
}
