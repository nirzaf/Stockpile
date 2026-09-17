using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Infrastructure.Services;

/// <summary>PostgreSQL-backed document identity, request replay, lifecycle and line-link service.</summary>
public sealed class DocumentIdentityService(
    InventoryDbContext context,
    IUnitOfWork unitOfWork,
    IDocumentNumberService documentNumberService) : IDocumentIdentityService
{
    private const string PurchaseOrderType = "PurchaseOrder";
    private const string PurchaseOrderLineType = "PurchaseOrderLine";
    private const string PurchaseOrderCreateScope = "PurchaseOrder.Create";

    public async Task<DocumentIdentity> CreateNumberedAsync(
        int companyId,
        string documentType,
        int period,
        string prefix,
        string requestScope,
        string requestKey,
        string requestHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestScope);
        ValidateRequestKey(requestKey);
        ValidateRequestHash(requestHash);
        if (companyId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(companyId));
        }
        var effectiveDocumentType = documentType.Trim();
        var effectivePrefix = prefix.Trim();
        if (effectiveDocumentType.Length > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(documentType), "A document type cannot exceed 64 characters.");
        }
        if (effectivePrefix.Length > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(prefix), "A number prefix cannot exceed 32 characters.");
        }

        DocumentIdentity? result = null;
        var effectiveScope = requestScope.Trim();
        if (effectiveScope.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(requestScope), "A request scope cannot exceed 256 characters.");
        }
        await unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            result = null;
            await AcquireRequestLockAsync(effectiveScope, requestKey, cancellationToken);

            var existing = await FindRequestAsync(effectiveScope, requestKey, cancellationToken);
            if (existing is not null)
            {
                EnsureSameRequest(existing, HashNumberedRequest(
                    companyId, effectiveDocumentType, existing.Period, effectivePrefix, requestHash));
                result = existing;
                return;
            }

            var companyExists = await context.Companies
                .AnyAsync(company => company.Id == companyId && company.IsActive, cancellationToken);
            if (!companyExists)
            {
                throw new InvalidOperationException("An active company in the current tenant is required to create a numbered document.");
            }

            var number = await documentNumberService.AllocateAsync(
                companyId,
                effectiveDocumentType,
                period,
                effectivePrefix,
                cancellationToken);
            var effectiveRequestHash = HashNumberedRequest(
                companyId, effectiveDocumentType, period, effectivePrefix, requestHash);
            result = DocumentIdentity.Create(
                DocumentIdentityId.New(),
                context.CurrentTenantId,
                companyId,
                effectiveDocumentType,
                number,
                period,
                DocumentLifecycleStatus.Draft,
                effectiveScope,
                requestKey,
                effectiveRequestHash);
            context.DocumentIdentities.Add(result);
        }, cancellationToken);

        return result ?? throw new InvalidOperationException("The document identity was not created.");
    }

    public async Task<PurchaseOrder> CreatePurchaseOrderAsync(
        PurchaseOrder purchaseOrder,
        IReadOnlyCollection<OrderDetail> details,
        string requestKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(purchaseOrder);
        ArgumentNullException.ThrowIfNull(details);
        ValidateRequestKey(requestKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(purchaseOrder.PONumber);
        purchaseOrder.PONumber = purchaseOrder.PONumber.Trim();
        if (purchaseOrder.PONumber.Length > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(purchaseOrder), "The PO number cannot exceed 50 characters.");
        }

        var requestHash = HashPurchaseOrderRequest(purchaseOrder, details);
        PurchaseOrder? result = null;
        await unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            result = null;
            await AcquireRequestLockAsync(PurchaseOrderCreateScope, requestKey, cancellationToken);

            var existing = await context.DocumentIdentities
                .Include(identity => identity.PurchaseOrder)
                .ThenInclude(order => order!.OrderDetails)
                .SingleOrDefaultAsync(identity =>
                    identity.RequestScope == PurchaseOrderCreateScope &&
                    identity.RequestKey == requestKey,
                    cancellationToken);
            if (existing is not null)
            {
                EnsureSameRequest(existing, requestHash);
                result = existing.PurchaseOrder ?? throw new InvalidOperationException(
                    "The idempotency key is mapped to a document that is not a purchase order.");
                return;
            }

            var identity = DocumentIdentity.Create(
                purchaseOrder.DocumentId,
                context.CurrentTenantId,
                companyId: null,
                PurchaseOrderType,
                purchaseOrder.PONumber,
                purchaseOrder.OrderDate.Year,
                ToDocumentLifecycle(purchaseOrder.Status),
                PurchaseOrderCreateScope,
                requestKey,
                requestHash);
            identity.PurchaseOrder = purchaseOrder;
            purchaseOrder.DocumentIdentity = identity;

            foreach (var detail in details)
            {
                detail.PurchaseOrder = purchaseOrder;
                if (!purchaseOrder.OrderDetails.Contains(detail))
                {
                    purchaseOrder.OrderDetails.Add(detail);
                }

                var lineIdentity = DocumentLineIdentity.Create(
                    detail.DocumentLineId,
                    purchaseOrder.DocumentId,
                    context.CurrentTenantId,
                    companyId: null,
                    PurchaseOrderLineType);
                lineIdentity.OrderDetail = detail;
                detail.DocumentLineIdentity = lineIdentity;
                detail.TenantId = context.CurrentTenantId;
                context.DocumentLineIdentities.Add(lineIdentity);
            }

            purchaseOrder.TenantId = context.CurrentTenantId;
            context.DocumentIdentities.Add(identity);
            context.PurchaseOrders.Add(purchaseOrder);
            result = purchaseOrder;
        }, cancellationToken);

        return result ?? throw new InvalidOperationException("The purchase order was not created.");
    }

    public async Task<PurchaseOrder?> TryReplayPurchaseOrderAsync(
        PurchaseOrder purchaseOrder,
        IReadOnlyCollection<OrderDetail> details,
        string requestKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(purchaseOrder);
        ArgumentNullException.ThrowIfNull(details);
        ValidateRequestKey(requestKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(purchaseOrder.PONumber);

        var requestHash = HashPurchaseOrderRequest(purchaseOrder, details);
        PurchaseOrder? result = null;
        await unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            result = null;
            await AcquireRequestLockAsync(PurchaseOrderCreateScope, requestKey, cancellationToken);
            var existing = await context.DocumentIdentities
                .Include(identity => identity.PurchaseOrder)
                .ThenInclude(order => order!.OrderDetails)
                .SingleOrDefaultAsync(identity =>
                    identity.RequestScope == PurchaseOrderCreateScope &&
                    identity.RequestKey == requestKey,
                    cancellationToken);
            if (existing is null)
                return;

            EnsureSameRequest(existing, requestHash);
            result = existing.PurchaseOrder ?? throw new InvalidOperationException(
                "The idempotency key is mapped to a document that is not a purchase order.");
        }, cancellationToken);

        return result;
    }

    public async Task LinkLinesAsync(
        DocumentLineIdentityId sourceLineId,
        DocumentLineIdentityId targetLineId,
        DocumentLineRelationshipType relationshipType,
        CancellationToken cancellationToken = default)
    {
        if (sourceLineId == targetLineId)
        {
            throw new ArgumentException("A document line cannot be linked to itself.", nameof(targetLineId));
        }
        if (!Enum.IsDefined(relationshipType))
        {
            throw new ArgumentOutOfRangeException(nameof(relationshipType));
        }

        var source = await context.DocumentLineIdentities
            .Include(line => line.DocumentIdentity)
            .SingleOrDefaultAsync(line => line.Id == sourceLineId, cancellationToken)
            ?? throw new InvalidOperationException("The source document line does not exist in the current tenant.");
        var target = await context.DocumentLineIdentities
            .Include(line => line.DocumentIdentity)
            .SingleOrDefaultAsync(line => line.Id == targetLineId, cancellationToken)
            ?? throw new InvalidOperationException("The target document line does not exist in the current tenant.");

        if (source.CompanyId != source.DocumentIdentity.CompanyId || target.CompanyId != target.DocumentIdentity.CompanyId)
        {
            throw new InvalidOperationException("Document-line company mapping must match its owning document.");
        }

        if (!source.CompanyId.HasValue || !target.CompanyId.HasValue)
        {
            throw new InvalidOperationException("Document lines must be mapped to a company before lineage can be recorded.");
        }

        if (source.CompanyId != target.CompanyId)
        {
            throw new InvalidOperationException("Cross-company document-line links are not allowed.");
        }

        context.DocumentLineLinks.Add(new DocumentLineLink
        {
            TenantId = context.CurrentTenantId,
            CompanyId = source.CompanyId.Value,
            SourceDocumentId = source.DocumentId,
            SourceLineId = source.Id,
            TargetDocumentId = target.DocumentId,
            TargetLineId = target.Id,
            RelationshipType = relationshipType,
            SourceLine = source,
            TargetLine = target
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task TransitionLifecycleAsync(
        DocumentIdentityId documentId,
        DocumentLifecycleStatus status,
        CancellationToken cancellationToken = default)
    {
        var identity = await context.DocumentIdentities
            .SingleOrDefaultAsync(document => document.Id == documentId, cancellationToken)
            ?? throw new InvalidOperationException("Document identity not found in the current tenant.");
        identity.TransitionTo(status);
    }

    private async Task<DocumentIdentity?> FindRequestAsync(
        string requestScope,
        string requestKey,
        CancellationToken cancellationToken) =>
        await context.DocumentIdentities.SingleOrDefaultAsync(
            identity => identity.RequestScope == requestScope && identity.RequestKey == requestKey,
            cancellationToken);

    private async Task AcquireRequestLockAsync(
        string requestScope,
        string requestKey,
        CancellationToken cancellationToken)
    {
        if (context.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            return;
        }

        var lockKey = string.Create(CultureInfo.InvariantCulture,
            $"{context.CurrentTenantId}\u001f{requestScope}\u001f{requestKey}");
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))",
            cancellationToken);
    }

    private static void EnsureSameRequest(DocumentIdentity existing, string requestHash)
    {
        if (!string.Equals(existing.RequestHash, requestHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The idempotency key was already used with a different document request.");
        }
    }

    private static void ValidateRequestKey(string requestKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestKey);
        if (requestKey.Length > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(requestKey), "An idempotency key cannot exceed 200 characters.");
        }
    }

    private static void ValidateRequestHash(string requestHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);
        if (requestHash.Length != 64 || requestHash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("A request hash must be a 64-character hexadecimal SHA-256 value.", nameof(requestHash));
        }
    }

    private static string HashPurchaseOrderRequest(PurchaseOrder purchaseOrder, IReadOnlyCollection<OrderDetail> details)
    {
        var payload = new StringBuilder();
        AppendField(payload, purchaseOrder.PONumber.Trim());
        AppendField(payload, purchaseOrder.SupplierId.ToString(CultureInfo.InvariantCulture));
        AppendField(payload, NormalizeDeliveryTerms(purchaseOrder.DeliveryTerms));
        AppendField(payload, purchaseOrder.Notes);
        AppendField(payload, purchaseOrder.Status.ToString());
        AppendField(payload, details.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var detail in details)
        {
            AppendField(payload, detail.ItemId.ToString(CultureInfo.InvariantCulture));
            AppendField(payload, detail.Quantity.ToString(CultureInfo.InvariantCulture));
            AppendField(payload, detail.UnitPrice.ToString("G29", CultureInfo.InvariantCulture));
            AppendField(payload, detail.TaxRuleId?.ToString(CultureInfo.InvariantCulture));
            AppendField(payload, detail.Direction.ToString());
            AppendField(payload, detail.DiscountPercent.ToString("G29", CultureInfo.InvariantCulture));
            AppendField(payload, detail.CurrencyScale.ToString(CultureInfo.InvariantCulture));
            // A tax-rule ID is the request input; its resolved rate/category/mode are
            // snapshots and must not make a retry conflict after the master is revised.
            if (!detail.TaxRuleId.HasValue)
            {
                AppendField(payload, detail.TaxCategory.ToString());
                AppendField(payload, detail.TaxMode.ToString());
                AppendField(payload, detail.TaxRatePercent.ToString("G29", CultureInfo.InvariantCulture));
            }
        }

        AppendField(payload, purchaseOrder.CurrencyScale.ToString(CultureInfo.InvariantCulture));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString())));
    }

    private static void AppendField(StringBuilder payload, string? value)
    {
        if (value is null)
        {
            payload.Append("-1:");
            return;
        }

        payload.Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value);
    }

    private static string? NormalizeDeliveryTerms(string? deliveryTerms) =>
        string.IsNullOrWhiteSpace(deliveryTerms) ? null : deliveryTerms.Trim();

    private static string HashNumberedRequest(int companyId, string documentType, int period, string prefix, string requestHash)
    {
        var payload = new StringBuilder();
        AppendField(payload, companyId.ToString(CultureInfo.InvariantCulture));
        AppendField(payload, documentType);
        AppendField(payload, period.ToString(CultureInfo.InvariantCulture));
        AppendField(payload, prefix);
        AppendField(payload, requestHash.ToUpperInvariant());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString())));
    }

    private static DocumentLifecycleStatus ToDocumentLifecycle(PurchaseOrderStatus status) => status switch
    {
        PurchaseOrderStatus.Draft => DocumentLifecycleStatus.Draft,
        PurchaseOrderStatus.Cancelled => DocumentLifecycleStatus.Cancelled,
        PurchaseOrderStatus.Voided => DocumentLifecycleStatus.Voided,
        _ => DocumentLifecycleStatus.Active
    };
}
