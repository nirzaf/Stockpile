using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Microsoft.Extensions.Logging;

namespace Merconiq.Core.Services;

/// <summary>
/// Purchase order service. Manages the full purchase order lifecycle from creation through
/// status transitions to deletion.
/// </summary>
public class PurchaseOrderService : IPurchaseOrderService
{
    private readonly IRepository<PurchaseOrder> _poRepo;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWebhookDispatcher _webhookDispatcher;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<PurchaseOrderService> _logger;

    public PurchaseOrderService(
        IRepository<PurchaseOrder> poRepo,
        IUnitOfWork unitOfWork,
        IWebhookDispatcher webhookDispatcher,
        ITenantContext tenantContext,
        ILogger<PurchaseOrderService> logger)
    {
        _poRepo = poRepo;
        _unitOfWork = unitOfWork;
        _webhookDispatcher = webhookDispatcher;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<PurchaseOrder>> GetAllAsync() => await _poRepo.GetAllAsync();

    /// <inheritdoc />
    public async Task<IEnumerable<PurchaseOrder>> GetPagedAsync(int page, int pageSize) => await _poRepo.GetPagedAsync(page, pageSize);

    /// <inheritdoc />
    public async Task<int> GetCountAsync() => await _poRepo.CountAsync();

    /// <inheritdoc />
    public async Task<PurchaseOrder?> GetByIdAsync(int id)
    {
        // Note: This relies on lazy loading or Include in a real implementation
        return await _poRepo.GetByIdAsync(id);
    }

    /// <inheritdoc />
    public async Task<PurchaseOrder> CreateAsync(PurchaseOrder purchaseOrder, List<OrderDetail> details)
    {
        purchaseOrder.OrderDate = DateTime.UtcNow;
        purchaseOrder.Status = PurchaseOrderStatus.Pending;
        purchaseOrder.TotalAmount = details.Sum(d => d.Quantity * d.UnitPrice);
        purchaseOrder.OrderDetails = details;

        _logger.LogInformation("Creating PO {PONumber}", purchaseOrder.PONumber);
        var created = await _poRepo.AddAsync(purchaseOrder);
        await _unitOfWork.SaveChangesAsync();
        return created;
    }

    /// <inheritdoc />
    public async Task UpdateStatusAsync(int id, string status)
    {
        var po = await _poRepo.GetByIdAsync(id);
        if (po == null) throw new InvalidOperationException("Purchase order not found");

        if (!Enum.TryParse<PurchaseOrderStatus>(status, ignoreCase: true, out var parsedStatus))
            throw new ArgumentException($"Invalid status: {status}");

        var previousStatus = po.Status;
        if (previousStatus != parsedStatus && !IsValidTransition(previousStatus, parsedStatus))
            throw new InvalidOperationException($"Invalid purchase order status transition: {previousStatus} -> {parsedStatus}");

        po.Status = parsedStatus;
        await _poRepo.UpdateAsync(po);
        if (previousStatus != parsedStatus)
        {
            await _webhookDispatcher.EnqueueAsync(WebhookEventFactory.Create(_tenantContext, "PurchaseOrder.StatusChanged", new
            {
                PurchaseOrderId = po.Id,
                PONumber = po.PONumber,
                PreviousStatus = previousStatus.ToString(),
                Status = parsedStatus.ToString()
            }));
        }
        await _unitOfWork.SaveChangesAsync();
        _logger.LogInformation("Updated PO {Id} status to {Status}", id, parsedStatus);
    }

    private static bool IsValidTransition(PurchaseOrderStatus current, PurchaseOrderStatus next) =>
        current switch
        {
            PurchaseOrderStatus.Draft => next is PurchaseOrderStatus.Pending or PurchaseOrderStatus.Cancelled,
            PurchaseOrderStatus.Pending => next is PurchaseOrderStatus.Submitted or PurchaseOrderStatus.Approved or PurchaseOrderStatus.Cancelled,
            PurchaseOrderStatus.Submitted => next is PurchaseOrderStatus.Approved or PurchaseOrderStatus.Cancelled,
            PurchaseOrderStatus.Approved => next is PurchaseOrderStatus.Received or PurchaseOrderStatus.Cancelled,
            PurchaseOrderStatus.Received or PurchaseOrderStatus.Cancelled => false,
            _ => false
        };

    /// <inheritdoc />
    public async Task DeleteAsync(int id)
    {
        var po = await _poRepo.GetByIdAsync(id);
        if (po != null)
        {
            if (po.Status is not (PurchaseOrderStatus.Draft or PurchaseOrderStatus.Pending))
                throw new InvalidOperationException($"Purchase order {id} in status {po.Status} cannot be deleted.");

            _logger.LogInformation("Deleting PO {Id}", id);
            await _poRepo.DeleteAsync(po);
            await _unitOfWork.SaveChangesAsync();
        }
    }
}
