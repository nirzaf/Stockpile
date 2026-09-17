using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Microsoft.Extensions.Logging;

namespace Merconiq.Core.Services;

/// <summary>
/// Purchase order service. Manages the full purchase order lifecycle from creation through
/// status transitions while preserving numbered-document history.
/// </summary>
public class PurchaseOrderService : IPurchaseOrderService
{
    private readonly IRepository<PurchaseOrder> _poRepo;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IDocumentIdentityService _documentIdentityService;
    private readonly IWebhookDispatcher _webhookDispatcher;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<PurchaseOrderService> _logger;
    private readonly IRepository<TaxRule>? _taxRuleRepository;

    public PurchaseOrderService(
        IRepository<PurchaseOrder> poRepo,
        IUnitOfWork unitOfWork,
        IDocumentIdentityService documentIdentityService,
        IWebhookDispatcher webhookDispatcher,
        ITenantContext tenantContext,
        ILogger<PurchaseOrderService> logger,
        IRepository<TaxRule>? taxRuleRepository = null)
    {
        _poRepo = poRepo;
        _unitOfWork = unitOfWork;
        _documentIdentityService = documentIdentityService;
        _webhookDispatcher = webhookDispatcher;
        _tenantContext = tenantContext;
        _logger = logger;
        _taxRuleRepository = taxRuleRepository;
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
    public async Task<PurchaseOrder> CreateAsync(
        PurchaseOrder purchaseOrder,
        List<OrderDetail> details,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(purchaseOrder);
        ArgumentNullException.ThrowIfNull(details);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        purchaseOrder.OrderDate = DateTime.UtcNow;
        purchaseOrder.Status = PurchaseOrderStatus.Pending;
        purchaseOrder.PONumber = purchaseOrder.PONumber.Trim();
        if (purchaseOrder.CurrencyScale is < 0 or > 4)
            throw new ArgumentOutOfRangeException(nameof(purchaseOrder.CurrencyScale));

        // Line scale is derived from the document currency. Normalize it before
        // the request hash is checked so retries use the same canonical inputs.
        foreach (var detail in details)
            detail.CurrencyScale = purchaseOrder.CurrencyScale;

        var replay = await _documentIdentityService.TryReplayPurchaseOrderAsync(
            purchaseOrder, details, idempotencyKey, cancellationToken);
        if (replay is not null)
            return replay;

        var calculationInputs = new List<DocumentLineAmount>(details.Count);
        foreach (var detail in details)
        {
            if (!detail.TaxRuleId.HasValue && detail.TaxRatePercent != 0m)
            {
                throw new InvalidOperationException("A configured tax rule is required for a non-zero tax rate.");
            }

            var taxRule = await ResolveTaxRuleAsync(detail.TaxRuleId, purchaseOrder.OrderDate, cancellationToken);
            if (taxRule is not null)
            {
                detail.TaxCategory = taxRule.Category;
                detail.TaxRatePercent = taxRule.RatePercent;
                detail.TaxMode = taxRule.CalculationMode;
                detail.TaxEffectiveFromUtc = taxRule.EffectiveFromUtc;
            }

            var calculated = DocumentAmountCalculator.Calculate(new DocumentLineAmount(
                detail.Quantity,
                detail.UnitPrice,
                detail.DiscountPercent,
                detail.TaxRatePercent,
                detail.TaxMode,
                purchaseOrder.CurrencyScale,
                detail.TaxCategory,
                detail.Direction));

            detail.CurrencyScale = calculated.CurrencyScale;
            detail.CalculationVersion = calculated.CalculationVersion;
            detail.NetAmount = calculated.NetAmount;
            detail.DiscountAmount = calculated.DiscountAmount;
            detail.TaxableAmount = calculated.TaxableAmount;
            detail.TaxAmount = calculated.TaxAmount;
            detail.GrossAmount = calculated.GrossAmount;
            calculationInputs.Add(new DocumentLineAmount(
                detail.Quantity,
                detail.UnitPrice,
                detail.DiscountPercent,
                detail.TaxRatePercent,
                detail.TaxMode,
                detail.CurrencyScale,
                detail.TaxCategory,
                detail.Direction));
        }

        var calculatedDocument = DocumentAmountCalculator.CalculateDocument(
            calculationInputs,
            purchaseOrder.CurrencyScale);
        purchaseOrder.NetAmount = calculatedDocument.NetAmount;
        purchaseOrder.DiscountAmount = calculatedDocument.DiscountAmount;
        purchaseOrder.TaxAmount = calculatedDocument.TaxAmount;
        purchaseOrder.TotalAmount = calculatedDocument.GrossAmount;
        purchaseOrder.CalculationVersion = calculatedDocument.CalculationVersion;
        purchaseOrder.OrderDetails = details;

        _logger.LogInformation("Creating PO {PONumber}", purchaseOrder.PONumber);
        var created = await _documentIdentityService.CreatePurchaseOrderAsync(
            purchaseOrder,
            details,
            idempotencyKey,
            cancellationToken);
        return created;
    }

    private async Task<TaxRule?> ResolveTaxRuleAsync(
        int? taxRuleId,
        DateTime effectiveAtUtc,
        CancellationToken cancellationToken)
    {
        if (!taxRuleId.HasValue)
        {
            return null;
        }

        if (_taxRuleRepository is null)
        {
            throw new InvalidOperationException("Tax-rule resolution is not configured for this purchase-order flow.");
        }

        var rules = (await _taxRuleRepository.FindAsync(
                rule => rule.Id == taxRuleId.Value && rule.IsActive))
            .Where(rule => rule.EffectiveFromUtc <= effectiveAtUtc &&
                (!rule.EffectiveToUtc.HasValue || rule.EffectiveToUtc > effectiveAtUtc))
            .ToList();

        return rules.Count switch
        {
            1 => rules[0],
            0 => throw new InvalidOperationException(
                $"Tax rule {taxRuleId.Value} is not active at {effectiveAtUtc:O}."),
            _ => throw new InvalidOperationException(
                $"Tax rule {taxRuleId.Value} has overlapping effective periods.")
        };
    }

    /// <inheritdoc />
    public async Task UpdateStatusAsync(int id, string status)
    {
        if (!Enum.TryParse<PurchaseOrderStatus>(status, ignoreCase: true, out var parsedStatus))
            throw new ArgumentException($"Invalid status: {status}");

        await _unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            var po = await _poRepo.GetByIdAsync(id);
            if (po == null) throw new InvalidOperationException("Purchase order not found");

            var previousStatus = po.Status;
            if (previousStatus != parsedStatus && !IsValidTransition(previousStatus, parsedStatus))
                throw new InvalidOperationException($"Invalid purchase order status transition: {previousStatus} -> {parsedStatus}");

            await _documentIdentityService.TransitionLifecycleAsync(
                po.DocumentId,
                ToDocumentLifecycle(parsedStatus));
            po.Status = parsedStatus;
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
        });
    }

    private static bool IsValidTransition(PurchaseOrderStatus current, PurchaseOrderStatus next) =>
        current switch
        {
            PurchaseOrderStatus.Draft => next is PurchaseOrderStatus.Pending or PurchaseOrderStatus.Cancelled,
            PurchaseOrderStatus.Pending => next is PurchaseOrderStatus.Submitted or PurchaseOrderStatus.Approved or PurchaseOrderStatus.Cancelled,
            PurchaseOrderStatus.Submitted => next is PurchaseOrderStatus.Approved or PurchaseOrderStatus.Cancelled or PurchaseOrderStatus.Voided,
            PurchaseOrderStatus.Approved => next is PurchaseOrderStatus.Received or PurchaseOrderStatus.Cancelled or PurchaseOrderStatus.Voided,
            PurchaseOrderStatus.Received or PurchaseOrderStatus.Cancelled or PurchaseOrderStatus.Voided => false,
            _ => false
        };

    private static DocumentLifecycleStatus ToDocumentLifecycle(PurchaseOrderStatus status) => status switch
    {
        PurchaseOrderStatus.Draft => DocumentLifecycleStatus.Draft,
        PurchaseOrderStatus.Cancelled => DocumentLifecycleStatus.Cancelled,
        PurchaseOrderStatus.Voided => DocumentLifecycleStatus.Voided,
        _ => DocumentLifecycleStatus.Active
    };

    /// <inheritdoc />
    public async Task DeleteAsync(int id)
    {
        await _unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            var po = await _poRepo.GetByIdAsync(id);
            if (po != null)
            {
                if (po.Status is not (PurchaseOrderStatus.Draft or PurchaseOrderStatus.Pending))
                    throw new InvalidOperationException($"Purchase order {id} in status {po.Status} cannot be deleted.");

                _logger.LogInformation("Cancelling PO {Id} without releasing its document number", id);
                await _documentIdentityService.TransitionLifecycleAsync(po.DocumentId, DocumentLifecycleStatus.Cancelled);
                po.Status = PurchaseOrderStatus.Cancelled;
                await _unitOfWork.SaveChangesAsync();
            }
        });
    }
}
