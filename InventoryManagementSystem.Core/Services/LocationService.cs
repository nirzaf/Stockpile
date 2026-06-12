using InventoryManagementSystem.Core.Entities;
using InventoryManagementSystem.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace InventoryManagementSystem.Core.Services;

/// <summary>Location service. Manages storage location CRUD and soft-delete lifecycle.</summary>
public class LocationService : ILocationService
{
    private readonly IRepository<Location> _repo;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<LocationService> _logger;

    public LocationService(IRepository<Location> repo, IUnitOfWork unitOfWork, ILogger<LocationService> logger)
    {
        _repo = repo;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Location>> GetAllAsync() => await _repo.GetAllAsync();

    /// <inheritdoc />
    public async Task<Location?> GetByIdAsync(int id) => await _repo.GetByIdAsync(id);

    /// <inheritdoc />
    public async Task<Location> CreateAsync(Location location)
    {
        _logger.LogInformation("Creating location {Name}", location.Name);
        var created = await _repo.AddAsync(location);
        await _unitOfWork.SaveChangesAsync();
        return created;
    }

    /// <inheritdoc />
    public async Task UpdateAsync(Location location)
    {
        _logger.LogInformation("Updating location {Id}", location.Id);
        await _repo.UpdateAsync(location);
        await _unitOfWork.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task DeleteAsync(int id)
    {
        var location = await _repo.GetByIdAsync(id);
        if (location != null)
        {
            _logger.LogInformation("Deleting location {Id}", id);
            await _repo.DeleteAsync(location);
            await _unitOfWork.SaveChangesAsync();
        }
    }
}
