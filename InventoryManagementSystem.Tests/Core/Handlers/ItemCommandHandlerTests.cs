using AutoFixture;
using FluentAssertions;
using InventoryManagementSystem.Core.Entities;
using InventoryManagementSystem.Core.Features.Items.Commands;
using InventoryManagementSystem.Core.Features.Items.Queries;
using InventoryManagementSystem.Core.Interfaces;
using InventoryManagementSystem.Tests.Common;
using Moq;
using Microsoft.Extensions.Logging.Abstractions;

namespace InventoryManagementSystem.Tests.Core.Handlers;

public class ItemCommandHandlerTests
{
    private readonly Fixture _fixture = InventoryFixtureFactory.Create();

    // === UpdateItemCommandHandler ===

    [Fact]
    public async Task UpdateItemCommandHandler_ItemExists_UpdatesItem()
    {
        var item = _fixture.Build<Item>().With(i => i.Id, 1).Create();
        var serviceMock = new Mock<IItemService>();
        serviceMock.Setup(s => s.GetByIdAsync(1)).ReturnsAsync(item);
        var handler = new UpdateItemCommandHandler(serviceMock.Object, NullLogger<UpdateItemCommandHandler>.Instance);

        var command = new UpdateItemCommand(1, "Updated desc", 25.50m, null);
        await handler.Handle(command, CancellationToken.None);

        serviceMock.Verify(s => s.UpdateAsync(
            It.Is<Item>(i => i.Description == "Updated desc" && i.Rate == 25.50m)), Times.Once);
    }

    [Fact]
    public async Task UpdateItemCommandHandler_ItemNotFound_ThrowsKeyNotFoundException()
    {
        var serviceMock = new Mock<IItemService>();
        serviceMock.Setup(s => s.GetByIdAsync(999)).ReturnsAsync((Item?)null);
        var handler = new UpdateItemCommandHandler(serviceMock.Object, NullLogger<UpdateItemCommandHandler>.Instance);

        var command = new UpdateItemCommand(999, "desc", 10m, null);
        var act = () => handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<KeyNotFoundException>()
            .WithMessage("*999*");
    }

    [Fact]
    public async Task UpdateItemCommandHandler_SetsSupplierId()
    {
        var item = _fixture.Build<Item>().With(i => i.Id, 1).With(i => i.SupplierId, (int?)null).Create();
        var serviceMock = new Mock<IItemService>();
        serviceMock.Setup(s => s.GetByIdAsync(1)).ReturnsAsync(item);
        var handler = new UpdateItemCommandHandler(serviceMock.Object, NullLogger<UpdateItemCommandHandler>.Instance);

        var command = new UpdateItemCommand(1, "desc", 10m, 5);
        await handler.Handle(command, CancellationToken.None);

        serviceMock.Verify(s => s.UpdateAsync(
            It.Is<Item>(i => i.SupplierId == 5)), Times.Once);
    }

    // === DeleteItemCommandHandler ===

    [Fact]
    public async Task DeleteItemCommandHandler_DelegatesToService()
    {
        var serviceMock = new Mock<IItemService>();
        var handler = new DeleteItemCommandHandler(serviceMock.Object, NullLogger<DeleteItemCommandHandler>.Instance);

        await handler.Handle(new DeleteItemCommand(42), CancellationToken.None);

        serviceMock.Verify(s => s.DeleteAsync(42), Times.Once);
    }

    // === SearchItemsQueryHandler ===

    [Fact]
    public async Task SearchItemsQueryHandler_DelegatesToService()
    {
        var items = _fixture.CreateMany<Item>(2).ToList();
        var serviceMock = new Mock<IItemService>();
        serviceMock.Setup(s => s.SearchAsync("widget")).ReturnsAsync(items);
        var handler = new SearchItemsQueryHandler(serviceMock.Object, NullLogger<SearchItemsQueryHandler>.Instance);

        var result = await handler.Handle(new SearchItemsQuery("widget"), CancellationToken.None);

        result.Should().BeEquivalentTo(items);
    }

    [Fact]
    public async Task SearchItemsQueryHandler_EmptyTerm_StillDelegates()
    {
        var serviceMock = new Mock<IItemService>();
        serviceMock.Setup(s => s.SearchAsync("")).ReturnsAsync(new List<Item>());
        var handler = new SearchItemsQueryHandler(serviceMock.Object, NullLogger<SearchItemsQueryHandler>.Instance);

        var result = await handler.Handle(new SearchItemsQuery(""), CancellationToken.None);

        result.Should().BeEmpty();
    }

    // === GetItemsPagedQueryHandler ===

    [Fact]
    public async Task GetItemsPagedQueryHandler_ReturnsPagedResultWithTotalCount()
    {
        var items = _fixture.CreateMany<Item>(5).ToList();
        var serviceMock = new Mock<IItemService>();
        serviceMock.Setup(s => s.GetPagedAsync(1, 5)).ReturnsAsync(items);
        serviceMock.Setup(s => s.GetCountAsync()).ReturnsAsync(20);
        var handler = new GetItemsPagedQueryHandler(serviceMock.Object, NullLogger<GetItemsPagedQueryHandler>.Instance);

        var result = await handler.Handle(new GetItemsPagedQuery(1, 5), CancellationToken.None);

        result.Items.Should().BeEquivalentTo(items);
        result.TotalCount.Should().Be(20);
    }
}
