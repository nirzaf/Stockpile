using AutoFixture;
using FluentAssertions;
using System.Linq.Expressions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Services;
using Merconiq.Tests.Common;
using Merconiq.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Caching.Memory;
using Moq;

namespace Merconiq.Tests.Core.Services;

public class ItemServiceTests
{
    private readonly Fixture _fixture = InventoryFixtureFactory.Create();
    private readonly Mock<IItemRepository> _repoMock = new();
    private readonly Mock<IRepository<UnitOfMeasure>> _unitRepoMock = new();
    private readonly Mock<IUnitOfWork> _uowMock = new();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly ItemService _sut;

    public ItemServiceTests()
    {
        _sut = new ItemService(
            _repoMock.Object,
            _uowMock.Object,
            NullLogger<ItemService>.Instance,
            _cache,
            new TestTenantContext("test-tenant"),
            _unitRepoMock.Object);
    }

    [Fact]
    public async Task GetByIdAsync_WhenItemExists_ReturnsItem()
    {
        // Arrange
        var item = _fixture.Create<Item>();
        _repoMock.Setup(r => r.GetByIdAsync(item.Id)).ReturnsAsync(item);

        // Act
        var result = await _sut.GetByIdAsync(item.Id);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(item);
        _repoMock.Verify(r => r.GetByIdAsync(item.Id), Times.Once);
    }

    [Fact]
    public async Task GetByIdAsync_WhenItemDoesNotExist_ReturnsNull()
    {
        // Arrange
        var id = _fixture.Create<int>();
        _repoMock.Setup(r => r.GetByIdAsync(id)).ReturnsAsync((Item?)null);

        // Act
        var result = await _sut.GetByIdAsync(id);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAllAsync_WhenItemsExist_ReturnsAllItems()
    {
        // Arrange
        var items = _fixture.CreateMany<Item>(3);
        _repoMock.Setup(r => r.GetAllAsync()).ReturnsAsync(items);

        // Act
        var result = await _sut.GetAllAsync();

        // Assert
        result.Should().BeEquivalentTo(items);
        _repoMock.Verify(r => r.GetAllAsync(), Times.Once);
    }

    [Fact]
    public async Task GetAllAsync_does_not_share_cached_items_between_tenants()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var tenantAItems = new[] { new Item { ItemCode = "TENANT-A" } };
        var tenantBItems = new[] { new Item { ItemCode = "TENANT-B" } };
        var tenantARepo = new Mock<IItemRepository>();
        var tenantBRepo = new Mock<IItemRepository>();
        tenantARepo.Setup(repository => repository.GetAllAsync()).ReturnsAsync(tenantAItems);
        tenantBRepo.Setup(repository => repository.GetAllAsync()).ReturnsAsync(tenantBItems);

        var tenantAService = new ItemService(
            tenantARepo.Object, _uowMock.Object, NullLogger<ItemService>.Instance, cache,
            new TestTenantContext("tenant-a"), _unitRepoMock.Object);
        var tenantBService = new ItemService(
            tenantBRepo.Object, _uowMock.Object, NullLogger<ItemService>.Instance, cache,
            new TestTenantContext("tenant-b"), _unitRepoMock.Object);

        (await tenantAService.GetAllAsync()).Single().ItemCode.Should().Be("TENANT-A");
        (await tenantBService.GetAllAsync()).Single().ItemCode.Should().Be("TENANT-B");
        tenantARepo.Verify(repository => repository.GetAllAsync(), Times.Once);
        tenantBRepo.Verify(repository => repository.GetAllAsync(), Times.Once);
    }

    [Fact]
    public async Task GetPagedAsync_WhenCalled_ForwardsPaginationArguments()
    {
        // Arrange
        var page = 2;
        var pageSize = 10;
        var items = _fixture.CreateMany<Item>(pageSize);
        _repoMock.Setup(r => r.GetPagedAsync(page, pageSize)).ReturnsAsync(items);

        // Act
        var result = await _sut.GetPagedAsync(page, pageSize);

        // Assert
        result.Should().BeEquivalentTo(items);
        _repoMock.Verify(r => r.GetPagedAsync(page, pageSize), Times.Once);
    }

    [Fact]
    public async Task GetCountAsync_WhenCalled_ReturnsCountFromRepository()
    {
        // Arrange
        var count = _fixture.Create<int>();
        _repoMock.Setup(r => r.CountAsync()).ReturnsAsync(count);

        // Act
        var result = await _sut.GetCountAsync();

        // Assert
        result.Should().Be(count);
    }

    [Fact]
    public async Task CreateAsync_WhenItemIsValid_AddsItemToRepository()
    {
        // Arrange
        var item = new Item { ItemCode = "SKU-VALID", Description = "Valid widget", Rate = 10m };
        _repoMock.Setup(r => r.AddAsync(item)).ReturnsAsync(item);

        // Act
        var result = await _sut.CreateAsync(item);

        // Assert
        result.Should().BeEquivalentTo(item);
        _repoMock.Verify(r => r.AddAsync(item), Times.Once);
    }

    [Fact]
    public async Task CreateAsync_rejects_unit_not_visible_to_current_tenant()
    {
        var item = new Item { ItemCode = "SKU-1", Description = "Widget", Rate = 10m, BaseUnitId = 42 };
        _unitRepoMock.Setup(r => r.FindAsync(It.IsAny<System.Linq.Expressions.Expression<Func<UnitOfMeasure, bool>>>()))
            .ReturnsAsync(Array.Empty<UnitOfMeasure>());

        var act = () => _sut.CreateAsync(item);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("Each item unit must exist and belong to the current tenant.");
        _repoMock.Verify(r => r.AddAsync(It.IsAny<Item>()), Times.Never);
    }

    [Fact]
    public async Task CreateAsync_rejects_invalid_conversion_factor()
    {
        var item = new Item { ItemCode = "SKU-1", Description = "Widget", Rate = 10m, PurchaseToBaseFactor = 0m };

        var act = () => _sut.CreateAsync(item);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("Purchase-to-base factor must be positive.*");
        _repoMock.Verify(r => r.AddAsync(It.IsAny<Item>()), Times.Never);
    }

    [Fact]
    public async Task CreateAsync_normalizes_and_persists_a_barcode()
    {
        _repoMock.Setup(r => r.FindAsync(It.IsAny<Expression<Func<Item, bool>>>()))
            .ReturnsAsync(Array.Empty<Item>());
        var item = new Item { ItemCode = " SKU-1 ", Description = " Widget ", Rate = 10m, Barcode = " 012345 " };
        _repoMock.Setup(r => r.AddAsync(item)).ReturnsAsync(item);

        await _sut.CreateAsync(item);

        item.ItemCode.Should().Be("SKU-1");
        item.Description.Should().Be("Widget");
        item.Barcode.Should().Be("012345");
        _repoMock.Verify(r => r.AddAsync(item), Times.Once);
    }

    [Fact]
    public async Task CreateAsync_rejects_duplicate_barcode()
    {
        _repoMock.Setup(r => r.FindAsync(It.IsAny<Expression<Func<Item, bool>>>()))
            .ReturnsAsync(new[] { new Item { Id = 2, Barcode = "012345" } });
        var item = new Item { ItemCode = "SKU-1", Description = "Widget", Rate = 10m, Barcode = "012345" };

        var act = () => _sut.CreateAsync(item);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("An item with this barcode already exists for this tenant.");
        _repoMock.Verify(r => r.AddAsync(It.IsAny<Item>()), Times.Never);
    }

    [Fact]
    public async Task CreateAsync_rejects_a_legacy_barcode_with_surrounding_whitespace()
    {
        _repoMock.Setup(r => r.FindAsync(It.IsAny<Expression<Func<Item, bool>>>()))
            .ReturnsAsync(new[] { new Item { Id = 2, Barcode = " 012345 " } });
        var item = new Item { ItemCode = "SKU-1", Description = "Widget", Rate = 10m, Barcode = "012345" };

        var act = () => _sut.CreateAsync(item);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("An item with this barcode already exists for this tenant.");
        _repoMock.Verify(r => r.AddAsync(It.IsAny<Item>()), Times.Never);
    }

    [Fact]
    public async Task CreateAsync_rejects_conversion_unit_without_base_unit()
    {
        var item = new Item
        {
            ItemCode = "SKU-1",
            Description = "Widget",
            Rate = 10m,
            PurchaseUnitId = 7,
            PurchaseToBaseFactor = 12m
        };

        var act = () => _sut.CreateAsync(item);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("A base unit is required when a purchase or sales unit is configured.");
    }

    [Fact]
    public async Task CreateAsync_rejects_fractional_precision_for_whole_only_unit()
    {
        _unitRepoMock.Setup(r => r.FindAsync(It.IsAny<Expression<Func<UnitOfMeasure, bool>>>()))
            .ReturnsAsync(new[] { new UnitOfMeasure { Id = 7, IsWholeUnitOnly = true } });
        var item = new Item
        {
            ItemCode = "SKU-1",
            Description = "Widget",
            Rate = 10m,
            BaseUnitId = 7,
            QuantityPrecision = 2
        };

        var act = () => _sut.CreateAsync(item);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("Items using a whole-unit-only unit must use zero quantity precision.");
    }

    [Fact]
    public async Task CreateAsync_rejects_deleted_unit()
    {
        _unitRepoMock.Setup(r => r.FindAsync(It.IsAny<Expression<Func<UnitOfMeasure, bool>>>()))
            .ReturnsAsync(new[] { new UnitOfMeasure { Id = 7, IsDeleted = true } });
        var item = new Item
        {
            ItemCode = "SKU-1",
            Description = "Widget",
            Rate = 10m,
            BaseUnitId = 7
        };

        var act = () => _sut.CreateAsync(item);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("Each item unit must exist and belong to the current tenant.");
    }

    [Fact]
    public async Task UpdateAsync_WhenItemExists_UpdatesItemInRepository()
    {
        // Arrange
        var item = new Item { ItemCode = "SKU-VALID", Description = "Valid widget", Rate = 10m };

        // Act
        await _sut.UpdateAsync(item);

        // Assert
        _repoMock.Verify(r => r.UpdateAsync(item), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_WhenItemExists_DeletesItemFromRepository()
    {
        // Arrange
        var item = new Item { Id = 1, ItemCode = "SKU-VALID", Description = "Valid widget", Rate = 10m };
        _repoMock.Setup(r => r.GetByIdAsync(item.Id)).ReturnsAsync(item);

        // Act
        await _sut.DeleteAsync(item.Id);

        // Assert
        _repoMock.Verify(r => r.GetByIdAsync(item.Id), Times.Once);
        _repoMock.Verify(r => r.DeleteAsync(item), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_WhenItemDoesNotExist_DoesNotCallDelete()
    {
        // Arrange
        var id = _fixture.Create<int>();
        _repoMock.Setup(r => r.GetByIdAsync(id)).ReturnsAsync((Item?)null);

        // Act
        await _sut.DeleteAsync(id);

        // Assert
        _repoMock.Verify(r => r.DeleteAsync(It.IsAny<Item>()), Times.Never);
    }

    [Fact]
    public async Task SearchAsync_WhenTermMatchesItemCode_ReturnsMatchingItems()
    {
        // Arrange
        var term = "WIDGET";
        var item = _fixture.Build<Item>().With(i => i.ItemCode, "WIDGET-001").Create();
        _repoMock.Setup(r => r.SearchAsync(term))
            .ReturnsAsync(new[] { item });

        // Act
        var result = await _sut.SearchAsync(term);

        // Assert
        result.Should().ContainSingle().Which.Should().BeEquivalentTo(item);
    }

    [Fact]
    public async Task SearchAsync_WhenTermIsEmpty_ReturnsNoItems()
    {
        var result = await _sut.SearchAsync("  ");

        result.Should().BeEmpty();
        _repoMock.Verify(r => r.SearchAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SearchAsync_WhenTermIsTooLong_ThrowsArgumentException()
    {
        var longTerm = new string('x', 101);

        var action = () => _sut.SearchAsync(longTerm);

        await action.Should().ThrowAsync<ArgumentException>();
        _repoMock.Verify(r => r.SearchAsync(It.IsAny<string>()), Times.Never);
    }
}
