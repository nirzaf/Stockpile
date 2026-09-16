using AutoFixture;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Features.Items.Commands;
using Merconiq.Core.Features.Items.Queries;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Merconiq.Tests.Core.Handlers;

public class ItemQueryHandlerTests
{
    private readonly Fixture _fixture = InventoryFixtureFactory.Create();

    [Fact]
    public async Task GetAllItemsQueryHandler_DelegatesToService()
    {
        var items = _fixture.CreateMany<Item>(3).ToList();
        var serviceMock = new Mock<IItemService>();
        serviceMock.Setup(s => s.GetAllAsync()).ReturnsAsync(items);
        var handler = new GetAllItemsQueryHandler(serviceMock.Object,
            NullLogger<GetAllItemsQueryHandler>.Instance);

        var result = await handler.Handle(new GetAllItemsQuery(), CancellationToken.None);

        result.Should().BeEquivalentTo(items);
        serviceMock.Verify(s => s.GetAllAsync(), Times.Once);
    }

    [Fact]
    public async Task GetItemByIdQueryHandler_Found_ReturnsItem()
    {
        var item = _fixture.Create<Item>();
        var serviceMock = new Mock<IItemService>();
        serviceMock.Setup(s => s.GetByIdAsync(item.Id)).ReturnsAsync(item);
        var handler = new GetItemByIdQueryHandler(serviceMock.Object,
            NullLogger<GetItemByIdQueryHandler>.Instance);

        var result = await handler.Handle(new GetItemByIdQuery(item.Id), CancellationToken.None);

        result.Should().Be(item);
    }

    [Fact]
    public async Task GetItemByIdQueryHandler_NotFound_ReturnsNull()
    {
        var serviceMock = new Mock<IItemService>();
        serviceMock.Setup(s => s.GetByIdAsync(It.IsAny<int>())).ReturnsAsync((Item?)null);
        var handler = new GetItemByIdQueryHandler(serviceMock.Object,
            NullLogger<GetItemByIdQueryHandler>.Instance);

        var result = await handler.Handle(new GetItemByIdQuery(999), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ForecastDemandHandler_DelegatesToService()
    {
        var forecast = new DemandForecastResult
        {
            ItemId = 1,
            ItemName = "TEST-001",
            ForecastedValues = [10f, 12f, 15f],
            ForecastHorizonDays = 3
        };
        var serviceMock = new Mock<IDemandForecastService>();
        serviceMock.Setup(s => s.ForecastDemandAsync(1, 30))
            .ReturnsAsync(forecast);
        var handler = new ForecastDemandHandler(serviceMock.Object);

        var result = await handler.Handle(new ForecastDemandQuery(1), CancellationToken.None);

        result.Should().BeEquivalentTo(forecast);
    }

    [Fact]
    public async Task ForecastAllItemsDemandHandler_DelegatesToService()
    {
        var forecasts = new List<DemandForecastResult>
        {
            new() { ItemId = 1, ItemName = "A" },
            new() { ItemId = 2, ItemName = "B" }
        };
        var serviceMock = new Mock<IDemandForecastService>();
        serviceMock.Setup(s => s.ForecastAllItemsAsync(14))
            .ReturnsAsync(forecasts);
        var handler = new ForecastAllItemsDemandHandler(serviceMock.Object);

        var result = await handler.Handle(new ForecastAllItemsDemandQuery(14), CancellationToken.None);

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task CreateItemCommandHandler_DelegatesToService()
    {
        var serviceMock = new Mock<IItemService>();
        var handler = new CreateItemCommandHandler(serviceMock.Object,
            NullLogger<CreateItemCommandHandler>.Instance);
        var cmd = new CreateItemCommand("CODE-001", "Description", 10.50m, null);

        await handler.Handle(cmd, CancellationToken.None);

        serviceMock.Verify(s => s.CreateAsync(
            It.Is<Item>(i => i.ItemCode == "CODE-001" && i.Rate == 10.50m)), Times.Once);
    }
}
