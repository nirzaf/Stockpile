using AutoFixture;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Features.Stock.Commands;
using Merconiq.Core.Features.Stock.Queries;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Merconiq.Tests.Core.Handlers;

public class StockHandlerTests
{
    private readonly Fixture _fixture = InventoryFixtureFactory.Create();

    [Fact]
    public async Task GetAllStockQueryHandler_DelegatesToService()
    {
        var stock = _fixture.CreateMany<StockInHand>(3).ToList();
        var serviceMock = new Mock<IStockService>();
        serviceMock.Setup(s => s.GetAllAsync()).ReturnsAsync(stock);
        var handler = new GetAllStockQueryHandler(serviceMock.Object,
            NullLogger<GetAllStockQueryHandler>.Instance);

        var result = await handler.Handle(new GetAllStockQuery(), CancellationToken.None);

        result.Should().BeEquivalentTo(stock);
    }

    [Fact]
    public async Task GetStockTransactionsQueryHandler_DelegatesToService()
    {
        var txs = _fixture.CreateMany<StockTransaction>(5).ToList();
        var from = DateTime.UtcNow.AddDays(-7);
        var to = DateTime.UtcNow;
        var serviceMock = new Mock<IStockService>();
        serviceMock.Setup(s => s.GetTransactionsAsync(from, to))
            .ReturnsAsync(txs);
        var handler = new GetStockTransactionsQueryHandler(serviceMock.Object,
            NullLogger<GetStockTransactionsQueryHandler>.Instance);

        var result = await handler.Handle(new GetStockTransactionsQuery(from, to), CancellationToken.None);

        result.Should().BeEquivalentTo(txs);
    }

    [Fact]
    public async Task GetStockByItemAndLocationQueryHandler_ForwardsLotIdentity()
    {
        var expiryDate = new DateTime(2030, 1, 1);
        var stock = _fixture.Create<StockInHand>();
        var serviceMock = new Mock<IStockService>();
        serviceMock.Setup(s => s.GetByItemAndLocationAsync(1, 2, "LOT-001", expiryDate))
            .ReturnsAsync(stock);
        var handler = new GetStockByItemAndLocationQueryHandler(serviceMock.Object,
            NullLogger<GetStockByItemAndLocationQueryHandler>.Instance);

        var result = await handler.Handle(
            new GetStockByItemAndLocationQuery(1, 2, "LOT-001", expiryDate), CancellationToken.None);

        result.Should().BeSameAs(stock);
        serviceMock.Verify(s => s.GetByItemAndLocationAsync(1, 2, "LOT-001", expiryDate), Times.Once);
    }

    [Fact]
    public async Task ReceiveStockCommandHandler_DelegatesToService()
    {
        var serviceMock = new Mock<IStockService>();
        var handler = new ReceiveStockCommandHandler(serviceMock.Object,
            NullLogger<ReceiveStockCommandHandler>.Instance);
        var cmd = new ReceiveStockCommand(1, 2, 25, "Notes");

        await handler.Handle(cmd, CancellationToken.None);

        serviceMock.Verify(s => s.ReceiveStockAsync(1, 2, 25, "Notes"), Times.Once);
    }

    [Fact]
    public async Task ReceiveStockCommandHandler_ForwardsLotIdentity()
    {
        var serviceMock = new Mock<IStockService>();
        var handler = new ReceiveStockCommandHandler(serviceMock.Object,
            NullLogger<ReceiveStockCommandHandler>.Instance);
        var expiryDate = new DateTime(2030, 1, 1);
        var cmd = new ReceiveStockCommand(1, 2, 25, "Notes", "LOT-001", expiryDate);

        await handler.Handle(cmd, CancellationToken.None);

        serviceMock.Verify(s => s.ReceiveStockAsync(1, 2, 25, "Notes", "LOT-001", expiryDate), Times.Once);
    }

    [Fact]
    public async Task TransferStockCommandHandler_DelegatesToService()
    {
        var serviceMock = new Mock<IStockService>();
        var handler = new TransferStockCommandHandler(serviceMock.Object,
            NullLogger<TransferStockCommandHandler>.Instance);
        var cmd = new TransferStockCommand(1, 10, 20, 50, "Transfer");

        await handler.Handle(cmd, CancellationToken.None);

        serviceMock.Verify(s => s.TransferStockAsync(1, 10, 20, 50, "Transfer"), Times.Once);
    }

    [Fact]
    public async Task SellStockCommandHandler_DelegatesToService()
    {
        var serviceMock = new Mock<IStockService>();
        var handler = new SellStockCommandHandler(serviceMock.Object,
            NullLogger<SellStockCommandHandler>.Instance);
        var cmd = new SellStockCommand(1, 5, 10, "Sale");

        await handler.Handle(cmd, CancellationToken.None);

        serviceMock.Verify(s => s.SellStockAsync(1, 5, 10, "Sale"), Times.Once);
    }

    [Fact]
    public async Task DetectAnomaliesQueryHandler_DelegatesToService()
    {
        var anomalies = new List<StockAnomaly>
        {
            new() { ItemId = 1, ItemName = "A", Date = DateTime.Today, ActualValue = 100,
                    ExpectedValue = 50, AnomalyType = "Spike", ConfidenceScore = 0.95 }
        };
        var serviceMock = new Mock<IAnomalyDetectionService>();
        serviceMock.Setup(s => s.DetectAnomaliesAsync(null, null))
            .ReturnsAsync(anomalies);
        var handler = new DetectAnomaliesHandler(serviceMock.Object);

        var result = await handler.Handle(new DetectAnomaliesQuery(), CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].AnomalyType.Should().Be("Spike");
    }
}
