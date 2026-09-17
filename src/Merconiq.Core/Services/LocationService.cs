using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Merconiq.Core.Services;

/// <summary>Location service. Manages storage location CRUD and soft-delete lifecycle.</summary>
public class LocationService : ILocationService
{
    private readonly IRepository<Location> _repo;
    private readonly IRepository<TransferOrder> _transferOrders;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<LocationService> _logger;

    public LocationService(
        IRepository<Location> repo,
        IRepository<TransferOrder> transferOrders,
        IUnitOfWork unitOfWork,
        ILogger<LocationService> logger)
    {
        _repo = repo;
        _transferOrders = transferOrders;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Location>> GetAllAsync() => await _repo.GetAllAsync();

    /// <inheritdoc />
    public async Task<IEnumerable<Location>> GetForCompaniesAsync(IReadOnlyCollection<int> companyIds)
    {
        if (companyIds.Count == 0)
        {
            return [];
        }

        return await _repo.FindAsync(location =>
            location.Branch != null && companyIds.Contains(location.Branch.CompanyId));
    }

    /// <inheritdoc />
    public async Task<Location?> GetByIdAsync(int id) => await _repo.GetByIdAsync(id);

    /// <inheritdoc />
    public async Task<Location> CreateAsync(Location location)
    {
        _logger.LogInformation("Creating location {Name}", location.Name);
        Location? created = null;
        await _unitOfWork.ExecuteMasterDataWriteAsync(async () =>
        {
            created = await _repo.AddAsync(location);
            await _unitOfWork.SaveChangesAsync();
        });
        return created!;
    }

    /// <inheritdoc />
    public async Task UpdateAsync(Location location)
    {
        _logger.LogInformation("Updating location {Id}", location.Id);
        await _unitOfWork.ExecuteMasterDataWriteAsync(async () =>
        {
            await _unitOfWork.AcquireTenantOperationLockAsync("organization-state");
            await _unitOfWork.AcquireLocationLocksAsync([location.Id]);
            var persisted = (await _repo.FindAsync(candidate => candidate.Id == location.Id)).SingleOrDefault();
            if (persisted is not null && persisted.BranchId != location.BranchId)
                await EnsureNoActiveTransferReferenceAsync(location.Id);
            await _repo.UpdateAsync(location);
            await _unitOfWork.SaveChangesAsync();
        });
    }

    /// <inheritdoc />
    public async Task DeleteAsync(int id)
    {
        await _unitOfWork.ExecuteMasterDataWriteAsync(async () =>
        {
            await _unitOfWork.AcquireTenantOperationLockAsync("organization-state");
            await _unitOfWork.AcquireLocationLocksAsync([id]);
            var location = await _repo.GetByIdAsync(id);
            if (location == null) return;
            await EnsureNoActiveTransferReferenceAsync(id);
            _logger.LogInformation("Deleting location {Id}", id);
            await _repo.DeleteAsync(location);
            await _unitOfWork.SaveChangesAsync();
        });
    }

    private async Task EnsureNoActiveTransferReferenceAsync(int locationId)
    {
        if (await _transferOrders.FindAsync(order =>
                order.Status != TransferOrderStatus.Cancelled &&
                (order.FromLocationId == locationId || order.ToLocationId == locationId)) is { } orders &&
            orders.Any())
        {
            throw new InvalidOperationException(
                "A location cannot be reassigned or deleted while an active transfer order references it.");
        }
    }
}
