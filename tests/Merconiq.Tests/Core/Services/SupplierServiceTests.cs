using AutoFixture;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Services;
using Merconiq.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Merconiq.Tests.Core.Services;

public class SupplierServiceTests
{
    private readonly Fixture _fixture = InventoryFixtureFactory.Create();
    private readonly Mock<IRepository<Supplier>> _repoMock = new();
    private readonly Mock<IUnitOfWork> _uowMock = new();
    private readonly SupplierService _sut;

    public SupplierServiceTests()
    {
        _uowMock.Setup(uow => uow.ExecuteInTransactionAsync(
                It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>(), It.IsAny<Func<Task<bool>>?>()))
            .Returns((Func<Func<Task>, CancellationToken, Func<Task<bool>>?, Task>)
                ((operation, _, _) => operation()));
        _sut = new SupplierService(_repoMock.Object, _uowMock.Object, NullLogger<SupplierService>.Instance);
    }

    [Fact]
    public async Task GetAllAsync_WhenSuppliersExist_ReturnsAll()
    {
        var suppliers = _fixture.CreateMany<Supplier>(3).ToList();
        _repoMock.Setup(r => r.GetAllAsync()).ReturnsAsync(suppliers);

        var result = await _sut.GetAllAsync();

        result.Should().BeEquivalentTo(suppliers);
    }

    [Fact]
    public async Task GetByIdAsync_WhenExists_ReturnsSupplier()
    {
        var supplier = _fixture.Create<Supplier>();
        _repoMock.Setup(r => r.GetByIdAsync(supplier.Id)).ReturnsAsync(supplier);

        var result = await _sut.GetByIdAsync(supplier.Id);

        result.Should().NotBeNull().And.BeEquivalentTo(supplier);
    }

    [Fact]
    public async Task GetByIdAsync_WhenNotExists_ReturnsNull()
    {
        _repoMock.Setup(r => r.GetByIdAsync(999)).ReturnsAsync((Supplier?)null);

        var result = await _sut.GetByIdAsync(999);

        result.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_AddsSupplierAndSaves()
    {
        var supplier = _fixture.Create<Supplier>();
        _repoMock.Setup(r => r.AddAsync(supplier)).ReturnsAsync(supplier);

        var result = await _sut.CreateAsync(supplier);

        result.Should().BeEquivalentTo(supplier);
        _repoMock.Verify(r => r.AddAsync(supplier), Times.Once);
        _uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateAsync_UpdatesSupplierAndSaves()
    {
        var supplier = _fixture.Create<Supplier>();

        await _sut.UpdateAsync(supplier);

        _repoMock.Verify(r => r.UpdateAsync(supplier), Times.Once);
        _uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_WhenExists_RemovesAndSaves()
    {
        var supplier = _fixture.Create<Supplier>();
        _repoMock.Setup(r => r.GetByIdAsync(supplier.Id)).ReturnsAsync(supplier);

        await _sut.DeleteAsync(supplier.Id);

        _repoMock.Verify(r => r.DeleteAsync(supplier), Times.Once);
        _uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_WhenNotExists_DoesNotCallDelete()
    {
        _repoMock.Setup(r => r.GetByIdAsync(999)).ReturnsAsync((Supplier?)null);

        await _sut.DeleteAsync(999);

        _repoMock.Verify(r => r.DeleteAsync(It.IsAny<Supplier>()), Times.Never);
        _uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
