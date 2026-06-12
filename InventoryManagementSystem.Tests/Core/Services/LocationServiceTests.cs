using AutoFixture;
using FluentAssertions;
using InventoryManagementSystem.Core.Entities;
using InventoryManagementSystem.Core.Interfaces;
using InventoryManagementSystem.Core.Services;
using InventoryManagementSystem.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace InventoryManagementSystem.Tests.Core.Services;

public class LocationServiceTests
{
    private readonly Fixture _fixture = InventoryFixtureFactory.Create();
    private readonly Mock<IRepository<Location>> _repoMock = new();
    private readonly Mock<IUnitOfWork> _uowMock = new();
    private readonly LocationService _sut;

    public LocationServiceTests()
    {
        _sut = new LocationService(_repoMock.Object, _uowMock.Object, NullLogger<LocationService>.Instance);
    }

    [Fact]
    public async Task GetAllAsync_WhenLocationsExist_ReturnsAll()
    {
        var locations = _fixture.CreateMany<Location>(3).ToList();
        _repoMock.Setup(r => r.GetAllAsync()).ReturnsAsync(locations);

        var result = await _sut.GetAllAsync();

        result.Should().BeEquivalentTo(locations);
    }

    [Fact]
    public async Task GetByIdAsync_WhenExists_ReturnsLocation()
    {
        var location = _fixture.Create<Location>();
        _repoMock.Setup(r => r.GetByIdAsync(location.Id)).ReturnsAsync(location);

        var result = await _sut.GetByIdAsync(location.Id);

        result.Should().NotBeNull().And.BeEquivalentTo(location);
    }

    [Fact]
    public async Task GetByIdAsync_WhenNotExists_ReturnsNull()
    {
        _repoMock.Setup(r => r.GetByIdAsync(999)).ReturnsAsync((Location?)null);

        var result = await _sut.GetByIdAsync(999);

        result.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_AddsLocationAndSaves()
    {
        var location = _fixture.Create<Location>();
        _repoMock.Setup(r => r.AddAsync(location)).ReturnsAsync(location);

        var result = await _sut.CreateAsync(location);

        result.Should().BeEquivalentTo(location);
        _repoMock.Verify(r => r.AddAsync(location), Times.Once);
        _uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateAsync_UpdatesLocationAndSaves()
    {
        var location = _fixture.Create<Location>();

        await _sut.UpdateAsync(location);

        _repoMock.Verify(r => r.UpdateAsync(location), Times.Once);
        _uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_WhenExists_RemovesAndSaves()
    {
        var location = _fixture.Create<Location>();
        _repoMock.Setup(r => r.GetByIdAsync(location.Id)).ReturnsAsync(location);

        await _sut.DeleteAsync(location.Id);

        _repoMock.Verify(r => r.DeleteAsync(location), Times.Once);
        _uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_WhenNotExists_DoesNotCallDelete()
    {
        _repoMock.Setup(r => r.GetByIdAsync(999)).ReturnsAsync((Location?)null);

        await _sut.DeleteAsync(999);

        _repoMock.Verify(r => r.DeleteAsync(It.IsAny<Location>()), Times.Never);
        _uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
