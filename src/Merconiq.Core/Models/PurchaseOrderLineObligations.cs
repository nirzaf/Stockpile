namespace Merconiq.Core.Models;

/// <summary>Ordered charge-line quantity recorded on the purchase order.</summary>
public sealed record PurchaseOrderLineObligation(
    int LineId,
    int ItemId,
    string? ItemCode,
    string? ItemDescription,
    int OrderedQuantity);

/// <summary>
/// Read-only ordered-line quantities. This view does not infer receipt, acceptance, rejection, or
/// outstanding quantities because posted goods-receipt source lines are not part of this slice.
/// </summary>
public sealed record PurchaseOrderLineObligations(
    int PurchaseOrderId,
    long OrderedQuantity,
    IReadOnlyList<PurchaseOrderLineObligation> Lines);
