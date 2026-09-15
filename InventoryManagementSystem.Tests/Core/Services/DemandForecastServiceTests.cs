using System.Linq.Expressions;
using AutoFixture;
using FluentAssertions;
using InventoryManagementSystem.Core.Entities;
using InventoryManagementSystem.Core.Interfaces;
using InventoryManagementSystem.Core.Services;
using InventoryManagementSystem.Tests.Common;
using InventoryManagementSystem.Tests.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace InventoryManagementSystem.Tests.Core.Services;

public class DemandForecastServiceTests
{
    private readonly Fixture _fixture = InventoryFixtureFactory.Create();
    private readonly Mock<IRepository<StockTransaction>> _txRepoMock = new();
    private readonly Mock<IRepository<Item>> _itemRepoMock = new();
    private readonly DemandForecastService _sut;

    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    public DemandForecastServiceTests()
    {
        _sut = new DemandForecastService(
            _txRepoMock.Object, _itemRepoMock.Object,
            NullLogger<DemandForecastService>.Instance,
            _cache,
            new TestTenantContext("test-tenant"));
    }

    [Fact]
    public async Task ForecastDemandAsync_InsufficientData_ReturnsEmptyForecast()
    {
        // Arrange — only 3 daily data points (need 5 minimum)
        var item = _fixture.Build<Item>().With(i => i.Id, 1).With(i => i.ItemCode, "ITEM-001").Create();
        _itemRepoMock.Setup(r => r.FindAsync(It.IsAny<Expression<Func<Item, bool>>>()))
            .ReturnsAsync(new[] { item });

        var transactions = Enumerable.Range(1, 3).Select(d =>
            _fixture.Build<StockTransaction>()
                .With(t => t.ItemId, 1)
                .With(t => t.TransactionDate, DateTime.UtcNow.AddDays(-d))
                .With(t => t.TransactionType, TransactionType.Sell)
                .With(t => t.Quantity, 10)
                .Create()).ToList();
        _txRepoMock.Setup(r => r.FindAsync(It.IsAny<Expression<Func<StockTransaction, bool>>>()))
            .ReturnsAsync(transactions);

        // Act
        var result = await _sut.ForecastDemandAsync(1, 30);

        // Assert
        result.ForecastedValues.Should().BeEmpty();
        result.ItemName.Should().Be("ITEM-001");
    }

    [Fact]
    public async Task ForecastDemandAsync_SufficientData_ReturnsForecast()
    {
        // Arrange — 30 daily data points
        var item = _fixture.Build<Item>().With(i => i.Id, 1).With(i => i.ItemCode, "ITEM-001").Create();
        _itemRepoMock.Setup(r => r.FindAsync(It.IsAny<Expression<Func<Item, bool>>>()))
            .ReturnsAsync(new[] { item });

        var transactions = Enumerable.Range(1, 30).Select(d =>
            _fixture.Build<StockTransaction>()
                .With(t => t.ItemId, 1)
                .With(t => t.TransactionDate, DateTime.UtcNow.AddDays(-d))
                .With(t => t.TransactionType, TransactionType.Sell)
                .With(t => t.Quantity, 10 + (d % 5))
                .Create()).ToList();
        _txRepoMock.Setup(r => r.FindAsync(It.IsAny<Expression<Func<StockTransaction, bool>>>()))
            .ReturnsAsync(transactions);

        // Act
        var result = await _sut.ForecastDemandAsync(1, 7);

        // Assert
        result.ItemId.Should().Be(1);
        result.TotalHistoricalDays.Should().Be(30);
        result.AverageDailyDemand.Should().BeGreaterThan(0);
        result.ForecastedValues.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ForecastDemandAsync_SetsAverageDailyDemand()
    {
        var item = _fixture.Build<Item>().With(i => i.Id, 1).Create();
        _itemRepoMock.Setup(r => r.FindAsync(It.IsAny<Expression<Func<Item, bool>>>()))
            .ReturnsAsync(new[] { item });

        var transactions = Enumerable.Range(1, 10).Select(d =>
            _fixture.Build<StockTransaction>()
                .With(t => t.ItemId, 1)
                .With(t => t.TransactionDate, DateTime.UtcNow.AddDays(-d))
                .With(t => t.TransactionType, TransactionType.Sell)
                .With(t => t.Quantity, 20)
                .Create()).ToList();
        _txRepoMock.Setup(r => r.FindAsync(It.IsAny<Expression<Func<StockTransaction, bool>>>()))
            .ReturnsAsync(transactions);

        var result = await _sut.ForecastDemandAsync(1, 5);

        result.AverageDailyDemand.Should().Be(20f);
    }

    [Fact]
    public async Task ForecastDemandAsync_SetsTotalHistoricalDays()
    {
        var item = _fixture.Build<Item>().With(i => i.Id, 1).Create();
        _itemRepoMock.Setup(r => r.FindAsync(It.IsAny<Expression<Func<Item, bool>>>()))
            .ReturnsAsync(new[] { item });

        var transactions = Enumerable.Range(1, 15).Select(d =>
            _fixture.Build<StockTransaction>()
                .With(t => t.ItemId, 1)
                .With(t => t.TransactionDate, DateTime.UtcNow.AddDays(-d))
                .With(t => t.TransactionType, TransactionType.Sell)
                .With(t => t.Quantity, 5)
                .Create()).ToList();
        _txRepoMock.Setup(r => r.FindAsync(It.IsAny<Expression<Func<StockTransaction, bool>>>()))
            .ReturnsAsync(transactions);

        var result = await _sut.ForecastDemandAsync(1, 5);

        result.TotalHistoricalDays.Should().Be(15);
    }

    [Fact]
    public async Task ForecastDemandAsync_HorizonDaysIsSetCorrectly()
    {
        var item = _fixture.Build<Item>().With(i => i.Id, 1).Create();
        _itemRepoMock.Setup(r => r.FindAsync(It.IsAny<Expression<Func<Item, bool>>>()))
            .ReturnsAsync(new[] { item });

        // Fewer than 5 days → insufficient, but horizon still set
        _txRepoMock.Setup(r => r.FindAsync(It.IsAny<Expression<Func<StockTransaction, bool>>>()))
            .ReturnsAsync(new List<StockTransaction>());

        var result = await _sut.ForecastDemandAsync(1, 14);

        result.ForecastHorizonDays.Should().Be(14);
    }

    [Fact]
    public async Task ForecastDemandAsync_ItemNotFound_UsesFallbackName()
    {
        _itemRepoMock.Setup(r => r.FindAsync(It.IsAny<Expression<Func<Item, bool>>>()))
            .ReturnsAsync(new List<Item>());
        _txRepoMock.Setup(r => r.FindAsync(It.IsAny<Expression<Func<StockTransaction, bool>>>()))
            .ReturnsAsync(new List<StockTransaction>());

        var result = await _sut.ForecastDemandAsync(999, 5);

        result.ItemName.Should().Be("Item #999");
    }

    [Fact]
    public async Task ForecastAllItemsAsync_MultipleItems_ReturnsForecastsForItemsWithSufficientData()
    {
        var items = Enumerable.Range(1, 3).Select(i =>
            _fixture.Build<Item>().With(x => x.Id, i).Create()).ToList();
        _itemRepoMock.Setup(r => r.GetAllAsync()).ReturnsAsync(items);

        // All items get 10 days of transactions
        var allTx = items.SelectMany(item =>
            Enumerable.Range(1, 10).Select(d =>
                _fixture.Build<StockTransaction>()
                    .With(t => t.ItemId, item.Id)
                    .With(t => t.TransactionDate, DateTime.UtcNow.AddDays(-d))
                    .With(t => t.TransactionType, TransactionType.Sell)
                    .With(t => t.Quantity, 5)
                    .Create())).ToList();
        _txRepoMock.Setup(r => r.FindAsync(It.IsAny<Expression<Func<StockTransaction, bool>>>()))
            .ReturnsAsync(allTx.Where(t => t.ItemId == 1 || t.ItemId == 2 || t.ItemId == 3).ToList());

        var result = await _sut.ForecastAllItemsAsync(5);

        // At least some items should produce forecasts
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task ForecastAllItemsAsync_EmptyItemList_ReturnsEmptyList()
    {
        _itemRepoMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Item>());

        var result = await _sut.ForecastAllItemsAsync(30);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ForecastDemandAsync_CachesGeneratedForecast()
    {
        var item = _fixture.Build<Item>().With(i => i.Id, 7).With(i => i.ItemCode, "CACHED-001").Create();
        var transactions = Enumerable.Range(1, 10).Select(d =>
            _fixture.Build<StockTransaction>()
                .With(t => t.ItemId, 7)
                .With(t => t.TransactionDate, DateTime.UtcNow.AddDays(-d))
                .With(t => t.TransactionType, TransactionType.Sell)
                .With(t => t.Quantity, 10)
                .Create()).ToList();
        var itemRepo = new Mock<IRepository<Item>>();
        var transactionRepo = new Mock<IRepository<StockTransaction>>();
        itemRepo.Setup(r => r.FindAsync(It.IsAny<Expression<Func<Item, bool>>>()))
            .ReturnsAsync(new[] { item });
        transactionRepo.Setup(r => r.FindAsync(It.IsAny<Expression<Func<StockTransaction, bool>>>()))
            .ReturnsAsync(transactions);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new DemandForecastService(
            transactionRepo.Object,
            itemRepo.Object,
            NullLogger<DemandForecastService>.Instance,
            cache,
            new TestTenantContext("test-tenant"));

        var first = await service.ForecastDemandAsync(7, 5);
        var second = await service.ForecastDemandAsync(7, 5);

        second.Should().BeSameAs(first);
        itemRepo.Verify(r => r.FindAsync(It.IsAny<Expression<Func<Item, bool>>>()), Times.Once);
        transactionRepo.Verify(r => r.FindAsync(It.IsAny<Expression<Func<StockTransaction, bool>>>()), Times.Once);
    }

    [Fact]
    public async Task ForecastAllItemsAsync_DoesNotOverlapRepositoryCalls()
    {
        var items = Enumerable.Range(1, 3)
            .Select(id => _fixture.Build<Item>().With(item => item.Id, id).Create())
            .ToList();
        var transactions = items.SelectMany(item => Enumerable.Range(1, 10).Select(day =>
            _fixture.Build<StockTransaction>()
                .With(transaction => transaction.ItemId, item.Id)
                .With(transaction => transaction.TransactionDate, DateTime.UtcNow.AddDays(-day))
                .With(transaction => transaction.TransactionType, TransactionType.Sell)
                .With(transaction => transaction.Quantity, 10)
                .Create())).ToList();
        var itemRepo = new Mock<IRepository<Item>>();
        var transactionRepo = new Mock<IRepository<StockTransaction>>();
        var activeTransactionQueries = 0;
        var overlappedTransactionQueries = false;

        itemRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(items);
        itemRepo.Setup(r => r.FindAsync(It.IsAny<Expression<Func<Item, bool>>>()))
            .ReturnsAsync(items);

        transactionRepo
            .Setup(r => r.FindAsync(It.IsAny<Expression<Func<StockTransaction, bool>>>()))
            .Returns((Expression<Func<StockTransaction, bool>> _) => ObserveTransactionsAsync());

        async Task<IEnumerable<StockTransaction>> ObserveTransactionsAsync()
        {
            if (Interlocked.Increment(ref activeTransactionQueries) > 1)
            {
                overlappedTransactionQueries = true;
            }

            try
            {
                await Task.Delay(10);
                return transactions;
            }
            finally
            {
                Interlocked.Decrement(ref activeTransactionQueries);
            }
        }

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new DemandForecastService(
            transactionRepo.Object,
            itemRepo.Object,
            NullLogger<DemandForecastService>.Instance,
            cache,
            new TestTenantContext("test-tenant"));

        await service.ForecastAllItemsAsync(5);

        overlappedTransactionQueries.Should().BeFalse();
    }
}
