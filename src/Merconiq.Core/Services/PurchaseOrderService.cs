using System.Text.Json;
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
    private readonly IRepository<OrderDetail>? _orderDetailRepository;
    private readonly IRepository<Supplier>? _supplierRepository;
    private readonly IRepository<Item>? _itemRepository;
    private readonly IRepository<UnitOfMeasure>? _unitRepository;
    private readonly IRepository<DocumentIdentity>? _documentRepository;

    public PurchaseOrderService(
        IRepository<PurchaseOrder> poRepo,
        IUnitOfWork unitOfWork,
        IDocumentIdentityService documentIdentityService,
        IWebhookDispatcher webhookDispatcher,
        ITenantContext tenantContext,
        ILogger<PurchaseOrderService> logger,
        IRepository<TaxRule>? taxRuleRepository = null,
        IRepository<OrderDetail>? orderDetailRepository = null,
        IRepository<Supplier>? supplierRepository = null,
        IRepository<Item>? itemRepository = null,
        IRepository<UnitOfMeasure>? unitRepository = null,
        IRepository<DocumentIdentity>? documentRepository = null)
    {
        _poRepo = poRepo;
        _unitOfWork = unitOfWork;
        _documentIdentityService = documentIdentityService;
        _webhookDispatcher = webhookDispatcher;
        _tenantContext = tenantContext;
        _logger = logger;
        _taxRuleRepository = taxRuleRepository;
        _orderDetailRepository = orderDetailRepository;
        _supplierRepository = supplierRepository;
        _itemRepository = itemRepository;
        _unitRepository = unitRepository;
        _documentRepository = documentRepository;
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
    public async Task<PurchaseOrder?> GetForAmendmentAsync(int id)
    {
        var order = await _poRepo.GetByIdAsync(id);
        if (order is null)
            return null;

        order.OrderDetails = (await RequireRepository(_orderDetailRepository)
                .FindAsync(line => line.PurchaseOrderId == id))
            .OrderBy(line => line.Id)
            .ToList();
        return order;
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
        purchaseOrder.CommercialVersion = 1;
        purchaseOrder.ApprovedCommercialVersion = null;
        purchaseOrder.ApprovedCommercialSnapshotJson = null;
        purchaseOrder.PONumber = purchaseOrder.PONumber.Trim();
        purchaseOrder.DeliveryTerms = NormalizeDeliveryTerms(purchaseOrder.DeliveryTerms);
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
        var po = await _poRepo.GetByIdAsync(id);
        if (po == null) throw new InvalidOperationException("Purchase order not found");

        if (!Enum.TryParse<PurchaseOrderStatus>(status, ignoreCase: true, out var parsedStatus))
            throw new ArgumentException($"Invalid status: {status}");

        var previousStatus = po.Status;
        if (previousStatus != parsedStatus && !IsValidTransition(previousStatus, parsedStatus))
            throw new InvalidOperationException($"Invalid purchase order status transition: {previousStatus} -> {parsedStatus}");

        if (previousStatus != parsedStatus && parsedStatus == PurchaseOrderStatus.Approved)
        {
            if (po.CommercialVersion < 1)
                throw new InvalidOperationException("A purchase order must have a valid commercial version before approval.");

            po.ApprovedCommercialSnapshotJson = await CaptureApprovedSnapshotAsync(po);
            po.ApprovedCommercialVersion = po.CommercialVersion;
        }

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
    }

    /// <inheritdoc />
    public async Task AmendApprovedAsync(int id, PurchaseOrderAmendment amendment)
    {
        ArgumentNullException.ThrowIfNull(amendment);
        ArgumentNullException.ThrowIfNull(amendment.Lines);
        if (amendment.CurrencyScale is < 0 or > 4)
            throw new ArgumentOutOfRangeException(nameof(amendment), "Currency precision must be between zero and four decimals.");

        var po = await _poRepo.GetByIdAsync(id)
            ?? throw new InvalidOperationException("Purchase order not found");
        if (po.Status != PurchaseOrderStatus.Approved)
            throw new InvalidOperationException("Only an approved purchase order can be amended.");
        if (po.ApprovedCommercialVersion != po.CommercialVersion ||
            string.IsNullOrWhiteSpace(po.ApprovedCommercialSnapshotJson))
            throw new InvalidOperationException("The current purchase-order version does not have a valid approval snapshot.");
        if (amendment.ExpectedCommercialVersion != po.CommercialVersion)
            throw new InvalidOperationException("The purchase order changed after this amendment form was opened. Reload and try again.");

        var linesRepository = RequireRepository(_orderDetailRepository);
        var existingLines = (await linesRepository.FindAsync(line => line.PurchaseOrderId == id))
            .OrderBy(line => line.Id)
            .ToList();
        var requestedLines = amendment.Lines.OrderBy(line => line.Id).ToList();
        if (requestedLines.Count != existingLines.Count ||
            !requestedLines.Select(line => line.Id).SequenceEqual(existingLines.Select(line => line.Id)) ||
            requestedLines.Any(line => line.Id <= 0))
        {
            throw new InvalidOperationException("This amendment must retain every existing PO line and its document-line identity.");
        }

        var supplierRepository = RequireRepository(_supplierRepository);
        var supplier = (await supplierRepository.FindAsync(candidate => candidate.Id == amendment.SupplierId))
            .SingleOrDefault()
            ?? throw new InvalidOperationException("The selected supplier does not exist in the current tenant.");
        var itemIds = requestedLines.Select(line => line.ItemId).Distinct().ToArray();
        var items = (await RequireRepository(_itemRepository)
                .FindAsync(item => itemIds.Contains(item.Id)))
            .ToDictionary(item => item.Id);
        if (items.Count != itemIds.Length)
            throw new InvalidOperationException("One or more selected items do not exist in the current tenant.");

        var materialChange = po.SupplierId != amendment.SupplierId ||
            po.DeliveryTerms != NormalizeDeliveryTerms(amendment.DeliveryTerms) ||
            po.Notes != amendment.Notes ||
            po.CurrencyScale != amendment.CurrencyScale ||
            requestedLines.Zip(existingLines).Any(pair =>
                pair.First.ItemId != pair.Second.ItemId ||
                pair.First.Quantity != pair.Second.Quantity ||
                pair.First.UnitPrice != pair.Second.UnitPrice ||
                pair.First.DiscountPercent != pair.Second.DiscountPercent ||
                pair.First.TaxRuleId != pair.Second.TaxRuleId ||
                pair.First.TaxRatePercent != pair.Second.TaxRatePercent ||
                pair.First.TaxCategory != pair.Second.TaxCategory ||
                pair.First.TaxMode != pair.Second.TaxMode ||
                pair.First.Direction != pair.Second.Direction);
        if (!materialChange)
            return;

        po.SupplierId = supplier.Id;
        po.DeliveryTerms = NormalizeDeliveryTerms(amendment.DeliveryTerms);
        po.Notes = amendment.Notes;
        po.CurrencyScale = amendment.CurrencyScale;
        var calculationInputs = new List<DocumentLineAmount>(existingLines.Count);
        for (var index = 0; index < existingLines.Count; index++)
        {
            var current = await linesRepository.GetByIdAsync(existingLines[index].Id)
                ?? throw new InvalidOperationException("A purchase-order line changed while the amendment was being applied.");
            if (current.PurchaseOrderId != id)
                throw new InvalidOperationException("A purchase-order line does not belong to this order.");
            var requested = requestedLines[index];
            if (requested.Quantity <= 0 || requested.ItemId <= 0 || requested.UnitPrice < 0m ||
                requested.DiscountPercent is < 0m or > 100m ||
                !Enum.IsDefined(requested.TaxCategory) || !Enum.IsDefined(requested.TaxMode) ||
                !Enum.IsDefined(requested.Direction))
                throw new InvalidOperationException("An amended PO line contains invalid commercial values.");
            if (!requested.TaxRuleId.HasValue && requested.TaxRatePercent != 0m)
                throw new InvalidOperationException("A configured tax rule is required for a non-zero tax rate.");

            var taxRule = await ResolveTaxRuleAsync(requested.TaxRuleId, DateTime.UtcNow, CancellationToken.None);
            current.ItemId = requested.ItemId;
            current.Quantity = requested.Quantity;
            current.UnitPrice = requested.UnitPrice;
            current.DiscountPercent = requested.DiscountPercent;
            current.TaxRuleId = requested.TaxRuleId;
            current.TaxCategory = taxRule?.Category ?? requested.TaxCategory;
            current.TaxRatePercent = taxRule?.RatePercent ?? requested.TaxRatePercent;
            current.TaxMode = taxRule?.CalculationMode ?? requested.TaxMode;
            current.TaxEffectiveFromUtc = taxRule?.EffectiveFromUtc;
            current.Direction = requested.Direction;
            current.CurrencyScale = amendment.CurrencyScale;

            var calculated = DocumentAmountCalculator.Calculate(new DocumentLineAmount(
                current.Quantity, current.UnitPrice, current.DiscountPercent, current.TaxRatePercent,
                current.TaxMode, amendment.CurrencyScale, current.TaxCategory, current.Direction));
            current.CalculationVersion = calculated.CalculationVersion;
            current.NetAmount = calculated.NetAmount;
            current.DiscountAmount = calculated.DiscountAmount;
            current.TaxableAmount = calculated.TaxableAmount;
            current.TaxAmount = calculated.TaxAmount;
            current.GrossAmount = calculated.GrossAmount;
            calculationInputs.Add(new DocumentLineAmount(
                current.Quantity, current.UnitPrice, current.DiscountPercent, current.TaxRatePercent,
                current.TaxMode, amendment.CurrencyScale, current.TaxCategory, current.Direction));
        }

        var totals = DocumentAmountCalculator.CalculateDocument(calculationInputs, amendment.CurrencyScale);
        po.NetAmount = totals.NetAmount;
        po.DiscountAmount = totals.DiscountAmount;
        po.TaxAmount = totals.TaxAmount;
        po.TotalAmount = totals.GrossAmount;
        po.CalculationVersion = totals.CalculationVersion;
        po.CommercialVersion = checked(po.CommercialVersion + 1);
        po.Status = PurchaseOrderStatus.Pending;

        await _webhookDispatcher.EnqueueAsync(WebhookEventFactory.Create(_tenantContext, "PurchaseOrder.StatusChanged", new
        {
            PurchaseOrderId = po.Id,
            PONumber = po.PONumber,
            PreviousStatus = PurchaseOrderStatus.Approved.ToString(),
            Status = PurchaseOrderStatus.Pending.ToString(),
            Reason = "CommercialAmendment",
            CommercialVersion = po.CommercialVersion
        }));
        await _unitOfWork.SaveChangesAsync();
        _logger.LogInformation("Amended PO {Id}; commercial version {Version} requires reapproval", id, po.CommercialVersion);
    }

    private async Task<string> CaptureApprovedSnapshotAsync(PurchaseOrder po)
    {
        var lines = (await RequireRepository(_orderDetailRepository)
                .FindAsync(line => line.PurchaseOrderId == po.Id))
            .OrderBy(line => line.DocumentLineId.Value)
            .ToList();
        var supplier = (await RequireRepository(_supplierRepository)
                .FindAsync(candidate => candidate.Id == po.SupplierId))
            .SingleOrDefault()
            ?? throw new InvalidOperationException("The purchase-order supplier does not exist in the current tenant.");
        var itemIds = lines.Select(line => line.ItemId).Distinct().ToArray();
        var items = (await RequireRepository(_itemRepository)
                .FindAsync(item => itemIds.Contains(item.Id)))
            .ToDictionary(item => item.Id);
        if (items.Count != itemIds.Length)
            throw new InvalidOperationException("One or more purchase-order items do not exist in the current tenant.");

        var unitIds = items.Values
            .Select(item => item.PurchaseUnitId ?? item.BaseUnitId)
            .Where(unitId => unitId.HasValue)
            .Select(unitId => unitId!.Value)
            .Distinct()
            .ToArray();
        var units = (await RequireRepository(_unitRepository)
                .FindAsync(unit => unitIds.Contains(unit.Id)))
            .ToDictionary(unit => unit.Id);
        var document = (await RequireRepository(_documentRepository)
                .FindAsync(candidate => candidate.Id == po.DocumentId))
            .SingleOrDefault();

        var lineSnapshots = lines.Select(line =>
        {
            var item = items[line.ItemId];
            var unitId = item.PurchaseUnitId ?? item.BaseUnitId;
            units.TryGetValue(unitId ?? 0, out var unit);
            return new PurchaseOrderCommercialLineSnapshot(
                line.DocumentLineId.Value, item.Id, item.ItemCode, item.Description,
                unitId, unit?.Code,
                item.PurchaseUnitId.HasValue ? item.PurchaseToBaseFactor : 1m,
                line.Quantity, line.UnitPrice, line.DiscountPercent, line.TaxRatePercent,
                line.TaxCategory.ToString(), line.TaxMode.ToString(), line.Direction.ToString(),
                line.CalculationVersion, line.NetAmount, line.DiscountAmount, line.TaxAmount, line.GrossAmount);
        }).ToList();

        var snapshot = new PurchaseOrderCommercialSnapshot(
            SchemaVersion: 1,
            TenantId: po.TenantId,
            DocumentId: po.DocumentId.Value,
            PONumber: po.PONumber,
            CompanyId: document?.CompanyId,
            SupplierId: supplier.Id,
            SupplierName: supplier.Name,
            SupplierAddress: supplier.Address,
            SupplierEmail: supplier.Email,
            DeliveryTerms: po.DeliveryTerms,
            Notes: po.Notes,
            CurrencyScale: po.CurrencyScale,
            CalculationVersion: po.CalculationVersion,
            NetAmount: po.NetAmount,
            DiscountAmount: po.DiscountAmount,
            TaxAmount: po.TaxAmount,
            TotalAmount: po.TotalAmount,
            Lines: lineSnapshots);
        return JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static string? NormalizeDeliveryTerms(string? terms)
    {
        var normalized = string.IsNullOrWhiteSpace(terms) ? null : terms.Trim();
        if (normalized?.Length > 1000)
            throw new ArgumentOutOfRangeException(nameof(terms), "Delivery terms cannot exceed 1000 characters.");
        return normalized;
    }

    private static TRepository RequireRepository<TRepository>(TRepository? repository)
        where TRepository : class => repository ?? throw new InvalidOperationException(
            $"{typeof(TRepository).Name} is required for purchase-order approval and amendment.");

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
    }
}
