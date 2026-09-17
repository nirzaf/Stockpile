using Merconiq.Core.Entities;

namespace Merconiq.Core.Interfaces;

/// <summary>Creates and links durable, tenant-safe business-document identities.</summary>
public interface IDocumentIdentityService
{
    /// <summary>
    /// Creates or replays a company-scoped numbered draft document. Reusing the request key
    /// with a different request hash is rejected.
    /// </summary>
    Task<DocumentIdentity> CreateNumberedAsync(
        int companyId,
        string documentType,
        int period,
        string prefix,
        string requestScope,
        string requestKey,
        string requestHash,
        CancellationToken cancellationToken = default);

    /// <summary>Creates or replays a tenant-scoped purchase order while preserving its supplied PO number.</summary>
    Task<PurchaseOrder> CreatePurchaseOrderAsync(
        PurchaseOrder purchaseOrder,
        IReadOnlyCollection<OrderDetail> details,
        string requestKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks for and returns a successful purchase-order replay before resolving mutable
    /// tax policy. Uses the same request lock and request hash as creation.
    /// </summary>
    Task<PurchaseOrder?> TryReplayPurchaseOrderAsync(
        PurchaseOrder purchaseOrder,
        IReadOnlyCollection<OrderDetail> details,
        string requestKey,
        CancellationToken cancellationToken = default);

    /// <summary>Adds a directed traceability link; both lines must belong to the same tenant and company.</summary>
    Task LinkLinesAsync(
        DocumentLineIdentityId sourceLineId,
        DocumentLineIdentityId targetLineId,
        DocumentLineRelationshipType relationshipType,
        CancellationToken cancellationToken = default);

    /// <summary>Tracks a lifecycle transition in the current scoped unit of work.</summary>
    Task TransitionLifecycleAsync(
        DocumentIdentityId documentId,
        DocumentLifecycleStatus status,
        CancellationToken cancellationToken = default);
}
