using System.Linq.Expressions;
using AutoFixture;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Exceptions;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Options;
using Merconiq.Core.Services;
using Merconiq.Tests.Common;
using Merconiq.Tests.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Merconiq.Tests.Core.Services;

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
            new TestTenantContext("test-tenant"),
            Options.Create(new ForecastingOptions()));
    }

    [Fact]
    public void ForecastingOptions_DefaultsToEnabled()
    {
        new ForecastingOptions().Enabled.Should().BeTrue();
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
        SetupBoundedFind(_txRepoMock, transactions);

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
        SetupBoundedFind(_txRepoMock, transactions);

        // Act
        var result = await _sut.ForecastDemandAsync(1, 7);

        // Assert
        result.ItemId.Should().Be(1);
        result.TotalHistoricalDays.Should().Be(30);
        result.AverageDailyDemand.Should().BeGreaterThan(0);
        result.ForecastedValues.Should().NotBeEmpty();
        result.ForecastingImplementation.Should().Be(ForecastingImplementations.ManagedMovingAverage);
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
        SetupBoundedFind(_txRepoMock, transactions);

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
        SetupBoundedFind(_txRepoMock, transactions);

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
        SetupBoundedFind(_txRepoMock, []);

        var result = await _sut.ForecastDemandAsync(1, 14);

        result.ForecastHorizonDays.Should().Be(14);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public async Task ForecastDemandAsync_RejectsHorizonOutsideConfiguredLimit(int horizonDays)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new DemandForecastService(
            _txRepoMock.Object,
            _itemRepoMock.Object,
            NullLogger<DemandForecastService>.Instance,
            cache,
            new TestTenantContext("test-tenant"),
            Options.Create(new ForecastingOptions { MaxForecastHorizonDays = 7 }));

        var act = () => service.ForecastDemandAsync(1, horizonDays);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        _itemRepoMock.Verify(repository => repository.FindAsync(It.IsAny<Expression<Func<Item, bool>>>()), Times.Never);
        _txRepoMock.Verify(repository => repository.FindPageAsync(
            It.IsAny<Expression<Func<StockTransaction, bool>>>(),
            It.IsAny<Func<IQueryable<StockTransaction>, IOrderedQueryable<StockTransaction>>>(),
            It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task ForecastDemandAsync_UsesBoundedUtcHistoryAndReportsLimitMetadata()
    {
        var asOf = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        var item = new Item { Id = 41, ItemCode = "BOUNDED-001" };
        var transactions = new[]
        {
            new StockTransaction { ItemId = item.Id, TransactionType = TransactionType.Sell, TransactionDate = new DateTime(2026, 1, 5), Quantity = 1000 },
            new StockTransaction { ItemId = item.Id, TransactionType = TransactionType.Sell, TransactionDate = new DateTime(2026, 1, 6), Quantity = 4 },
            new StockTransaction { ItemId = item.Id, TransactionType = TransactionType.Receive, TransactionDate = new DateTime(2026, 1, 8), Quantity = 40 },
            new StockTransaction { ItemId = item.Id, TransactionType = TransactionType.Transfer, TransactionDate = new DateTime(2026, 1, 8), Quantity = 50 },
            new StockTransaction { ItemId = item.Id, TransactionType = TransactionType.Sell, TransactionDate = new DateTime(2026, 1, 9), Quantity = 8 },
            new StockTransaction { ItemId = item.Id, TransactionType = TransactionType.Sell, TransactionDate = new DateTime(2026, 1, 10), Quantity = 8 },
            new StockTransaction { ItemId = item.Id, TransactionType = TransactionType.Sell, TransactionDate = new DateTime(2026, 1, 11), Quantity = 2000 }
        };

        var itemRepository = new Mock<IRepository<Item>>();
        itemRepository.Setup(repository => repository.FindAsync(It.IsAny<Expression<Func<Item, bool>>>() ))
            .ReturnsAsync(new[] { item });
        var transactionRepository = new Mock<IRepository<StockTransaction>>();
        SetupBoundedFind(transactionRepository, transactions);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new DemandForecastService(
            transactionRepository.Object,
            itemRepository.Object,
            NullLogger<DemandForecastService>.Instance,
            cache,
            new TestTenantContext("test-tenant"),
            Options.Create(new ForecastingOptions
            {
                MaxForecastHorizonDays = 7,
                MaxHistoricalDays = 5
            }),
            new FixedTimeProvider(asOf));

        var result = await service.ForecastDemandAsync(item.Id, 3);

        result.ForecastingImplementation.Should().Be(ForecastingImplementations.ManagedMovingAverage);
        result.ForecastingImplementationVersion.Should().Be(ForecastingImplementations.ManagedMovingAverageVersion);
        result.MaxForecastHorizonDays.Should().Be(7);
        result.MaxHistoricalDays.Should().Be(5);
        result.DataWindowStartDate.Should().Be(new DateOnly(2026, 1, 6));
        result.DataWindowEndDate.Should().Be(new DateOnly(2026, 1, 10));
        result.TotalHistoricalDays.Should().Be(5);
        result.AverageDailyDemand.Should().Be(4);
        result.ForecastedValues.Should().Equal(4, 4, 4);
        result.GeneratedAt.Should().Be(asOf.UtcDateTime);
        result.KnownLimitations.Should().Contain(limitation => limitation.Contains("return", StringComparison.OrdinalIgnoreCase));
        result.KnownLimitations.Should().Contain(limitation => limitation.Contains("stockout", StringComparison.OrdinalIgnoreCase));
        result.KnownLimitations.Should().Contain(limitation => limitation.Contains("hard limits", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ForecastDemandAsync_ItemNotFound_UsesFallbackName()
    {
        _itemRepoMock.Setup(r => r.FindAsync(It.IsAny<Expression<Func<Item, bool>>>()))
            .ReturnsAsync(new List<Item>());
        SetupBoundedFind(_txRepoMock, []);

        var result = await _sut.ForecastDemandAsync(999, 5);

        result.ItemName.Should().Be("Item #999");
    }

    [Fact]
    public async Task ForecastAllItemsAsync_MultipleItems_ReturnsForecastsForItemsWithSufficientData()
    {
        var items = Enumerable.Range(1, 3).Select(i =>
            _fixture.Build<Item>().With(x => x.Id, i).Create()).ToList();
        SetupBoundedFind(_itemRepoMock, items);

        // All items get 10 days of transactions
        var allTx = items.SelectMany(item =>
            Enumerable.Range(1, 10).Select(d =>
                _fixture.Build<StockTransaction>()
                    .With(t => t.ItemId, item.Id)
                    .With(t => t.TransactionDate, DateTime.UtcNow.AddDays(-d))
                    .With(t => t.TransactionType, TransactionType.Sell)
                    .With(t => t.Quantity, 5)
                    .Create())).ToList();
        SetupBoundedFind(_txRepoMock, allTx);

        var result = await _sut.ForecastAllItemsAsync(5);

        // At least some items should produce forecasts
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task ForecastAllItemsAsync_EmptyItemList_ReturnsEmptyList()
    {
        SetupBoundedFind(_itemRepoMock, []);

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
        SetupBoundedFind(transactionRepo, transactions);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new DemandForecastService(
            transactionRepo.Object,
            itemRepo.Object,
            NullLogger<DemandForecastService>.Instance,
            cache,
            new TestTenantContext("test-tenant"),
            Options.Create(new ForecastingOptions()));

        var first = await service.ForecastDemandAsync(7, 5);
        var second = await service.ForecastDemandAsync(7, 5);

        second.Should().BeSameAs(first);
        itemRepo.Verify(r => r.FindAsync(It.IsAny<Expression<Func<Item, bool>>>()), Times.Once);
        transactionRepo.Verify(r => r.FindPageAsync(
            It.IsAny<Expression<Func<StockTransaction, bool>>>(),
            It.IsAny<Func<IQueryable<StockTransaction>, IOrderedQueryable<StockTransaction>>>(),
            It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task ForecastDemandForCompaniesAsync_FiltersHistoryAndSeparatesCompanyCacheEntries()
    {
        var item = new Item { Id = 7, ItemCode = "SCOPED-001" };
        var transactions = Enumerable.Range(1, 10).SelectMany(day => new[]
        {
            new StockTransaction
            {
                ItemId = 7,
                TransactionType = TransactionType.Sell,
                TransactionDate = DateTime.UtcNow.Date.AddDays(-day),
                Quantity = 10,
                FromLocation = new Location { Branch = new Branch { CompanyId = 1 } }
            },
            new StockTransaction
            {
                ItemId = 7,
                TransactionType = TransactionType.Sell,
                TransactionDate = DateTime.UtcNow.Date.AddDays(-day),
                Quantity = 90,
                FromLocation = new Location { Branch = new Branch { CompanyId = 2 } }
            }
        }).ToList();

        var itemRepository = new Mock<IRepository<Item>>();
        itemRepository.Setup(repository => repository.FindAsync(It.IsAny<Expression<Func<Item, bool>>>() ))
            .ReturnsAsync(new[] { item });
        var transactionRepository = new Mock<IRepository<StockTransaction>>();
        SetupBoundedFind(transactionRepository, transactions);
        using var scopedCache = new MemoryCache(new MemoryCacheOptions());
        var service = new DemandForecastService(
            transactionRepository.Object,
            itemRepository.Object,
            NullLogger<DemandForecastService>.Instance,
            scopedCache,
            new TestTenantContext("test-tenant"),
            Options.Create(new ForecastingOptions()));

        var companyOne = await service.ForecastDemandForCompaniesAsync(7, 5, [1]);
        var companyTwo = await service.ForecastDemandForCompaniesAsync(7, 5, [2]);
        var companyOneCached = await service.ForecastDemandForCompaniesAsync(7, 5, [1]);

        companyOne.AverageDailyDemand.Should().Be(10);
        companyTwo.AverageDailyDemand.Should().Be(90);
        companyOneCached.Should().BeSameAs(companyOne);
        transactionRepository.Verify(repository => repository.FindPageAsync(
                It.IsAny<Expression<Func<StockTransaction, bool>>>(),
                It.IsAny<Func<IQueryable<StockTransaction>, IOrderedQueryable<StockTransaction>>>(),
                It.IsAny<int>()),
            Times.Exactly(2));
    }

    [Fact]
    public void BuildDailyDemand_ignores_transfers_and_fills_missing_calendar_days()
    {
        var start = new DateTime(2026, 1, 1);
        var observations = DemandForecastDataPreparation.BuildDailyDemand(
        [
            new StockTransaction { TransactionType = TransactionType.Sell, TransactionDate = start, Quantity = 5 },
            new StockTransaction { TransactionType = TransactionType.Transfer, TransactionDate = start.AddDays(1), Quantity = 100 },
            new StockTransaction { TransactionType = TransactionType.Receive, TransactionDate = start.AddDays(2), Quantity = 200 },
            new StockTransaction { TransactionType = TransactionType.Sell, TransactionDate = start.AddDays(3), Quantity = 7 }
        ]);

        observations.Select(observation => observation.Date)
            .Should().Equal(start, start.AddDays(1), start.AddDays(2), start.AddDays(3));
        observations.Select(observation => observation.Quantity)
            .Should().Equal(5, 0, 0, 7);
    }

    [Fact]
    public void BuildDailyDemand_returns_empty_for_transfer_only_activity()
    {
        var observations = DemandForecastDataPreparation.BuildDailyDemand(
        [
            new StockTransaction
            {
                TransactionType = TransactionType.Transfer,
                TransactionDate = new DateTime(2026, 1, 1),
                Quantity = 100
            }
        ]);

        observations.Should().BeEmpty();
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

        SetupBoundedFind(itemRepo, items);
        itemRepo.Setup(r => r.FindAsync(It.IsAny<Expression<Func<Item, bool>>>()))
            .ReturnsAsync(items);

        transactionRepo
            .Setup(r => r.FindPageAsync(
                It.IsAny<Expression<Func<StockTransaction, bool>>>(),
                It.IsAny<Func<IQueryable<StockTransaction>, IOrderedQueryable<StockTransaction>>>(),
                It.IsAny<int>()))
            .Returns((Expression<Func<StockTransaction, bool>> predicate,
                Func<IQueryable<StockTransaction>, IOrderedQueryable<StockTransaction>> orderBy,
                int maxResults) => ObserveTransactionsAsync(predicate, orderBy, maxResults));

        async Task<IEnumerable<StockTransaction>> ObserveTransactionsAsync(
            Expression<Func<StockTransaction, bool>> predicate,
            Func<IQueryable<StockTransaction>, IOrderedQueryable<StockTransaction>> orderBy,
            int maxResults)
        {
            if (Interlocked.Increment(ref activeTransactionQueries) > 1)
            {
                overlappedTransactionQueries = true;
            }

            try
            {
                await Task.Delay(10);
                return orderBy(transactions.AsQueryable().Where(predicate))
                    .Take(maxResults)
                    .ToList();
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
            new TestTenantContext("test-tenant"),
            Options.Create(new ForecastingOptions()));

        await service.ForecastAllItemsAsync(5);

        overlappedTransactionQueries.Should().BeFalse();
    }

    [Fact]
    public void ForecastingEvaluation_UsesChronologicalHoldoutAndReportsBaseline()
    {
        // The managed implementation forecasts the training mean. Keep the
        // holdout strictly after training so this fixture cannot leak future data.
        var observations = new[] { 4f, 6f, 8f, 10f, 12f, 14f, 16f, 18f };
        var training = observations.Take(5).ToArray();
        var holdout = observations.Skip(5).ToArray();

        var managedPrediction = training.Average();
        var naivePrediction = training[^1];
        var managedMae = holdout.Average(value => Math.Abs(value - managedPrediction));
        var naiveMae = holdout.Average(value => Math.Abs(value - naivePrediction));

        training.Should().HaveCount(5);
        holdout.Should().Equal(14f, 16f, 18f);
        managedPrediction.Should().Be(8f);
        managedMae.Should().Be(8f);
        naiveMae.Should().Be(4f);
    }

    [Fact]
    public async Task ForecastDemandAsync_RejectsRawTransactionOverflowInsteadOfReturningPartialForecast()
    {
        var item = new Item { Id = 7, ItemCode = "ROW-LIMIT" };
        var transactions = Enumerable.Range(1, 3).Select(day => new StockTransaction
        {
            Id = day,
            ItemId = item.Id,
            TransactionType = TransactionType.Sell,
            TransactionDate = DateTime.UtcNow.Date.AddDays(-day),
            Quantity = 2
        }).ToList();
        var itemRepo = new Mock<IRepository<Item>>();
        itemRepo.Setup(repository => repository.FindAsync(It.IsAny<Expression<Func<Item, bool>>>() ))
            .ReturnsAsync([item]);
        var txRepo = new Mock<IRepository<StockTransaction>>();
        SetupBoundedFind(txRepo, transactions);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new DemandForecastService(
            txRepo.Object,
            itemRepo.Object,
            NullLogger<DemandForecastService>.Instance,
            cache,
            new TestTenantContext("test-tenant"),
            Options.Create(new ForecastingOptions { MaxHistoricalTransactionsPerForecast = 2 }));

        var act = () => service.ForecastDemandAsync(item.Id, 5);

        var exception = await act.Should().ThrowAsync<ForecastResourceLimitExceededException>();
        exception.Which.Resource.Should().Be(nameof(ForecastingOptions.MaxHistoricalTransactionsPerForecast));
        exception.Which.Maximum.Should().Be(2);
        exception.Which.ObservedAtLeast.Should().Be(3);
        txRepo.Verify(repository => repository.FindPageAsync(
            It.IsAny<Expression<Func<StockTransaction, bool>>>(),
            It.IsAny<Func<IQueryable<StockTransaction>, IOrderedQueryable<StockTransaction>>>(),
            3), Times.Once);
    }

    [Fact]
    public async Task ForecastAllItemsAsync_RejectsCatalogOverflowInsteadOfReturningPartialList()
    {
        var items = Enumerable.Range(1, 3).Select(id => new Item { Id = id, ItemCode = $"ITEM-{id}" }).ToList();
        var itemRepo = new Mock<IRepository<Item>>();
        SetupBoundedFind(itemRepo, items);
        var txRepo = new Mock<IRepository<StockTransaction>>();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new DemandForecastService(
            txRepo.Object,
            itemRepo.Object,
            NullLogger<DemandForecastService>.Instance,
            cache,
            new TestTenantContext("test-tenant"),
            Options.Create(new ForecastingOptions { MaxItemsPerAllItemsForecast = 2 }));

        var act = () => service.ForecastAllItemsAsync(5);

        var exception = await act.Should().ThrowAsync<ForecastResourceLimitExceededException>();
        exception.Which.Resource.Should().Be(nameof(ForecastingOptions.MaxItemsPerAllItemsForecast));
        exception.Which.Maximum.Should().Be(2);
        itemRepo.Verify(repository => repository.FindPageAsync(
            It.IsAny<Expression<Func<Item, bool>>>(),
            It.IsAny<Func<IQueryable<Item>, IOrderedQueryable<Item>>>(),
            3), Times.Once);
        txRepo.Verify(repository => repository.FindPageAsync(
            It.IsAny<Expression<Func<StockTransaction, bool>>>(),
            It.IsAny<Func<IQueryable<StockTransaction>, IOrderedQueryable<StockTransaction>>>(),
            It.IsAny<int>()), Times.Never);
    }

    [Theory]
    [InlineData(0, 250)]
    [InlineData(250_001, 250)]
    [InlineData(1, 0)]
    [InlineData(1, 2_501)]
    public void ForecastingOptions_RejectsResourceLimitsOutsideHardCeilings(
        int maxHistoricalTransactions,
        int maxAllItems)
    {
        ForecastingOptions.HasValidResourceLimits(new ForecastingOptions
        {
            MaxHistoricalTransactionsPerForecast = maxHistoricalTransactions,
            MaxItemsPerAllItemsForecast = maxAllItems
        }).Should().BeFalse();
    }

    private static void SetupBoundedFind<T>(Mock<IRepository<T>> repository, IEnumerable<T> values)
        where T : class
    {
        repository.Setup(repo => repo.FindPageAsync(
                It.IsAny<Expression<Func<T, bool>>>(),
                It.IsAny<Func<IQueryable<T>, IOrderedQueryable<T>>>(),
                It.IsAny<int>()))
            .Returns((Expression<Func<T, bool>> predicate,
                Func<IQueryable<T>, IOrderedQueryable<T>> orderBy,
                int maxResults) => Task.FromResult<IEnumerable<T>>(
                    orderBy(values.AsQueryable().Where(predicate))
                        .Take(maxResults)
                        .ToList()));
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
