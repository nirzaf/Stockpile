using Merconiq.Core.Entities;

namespace Merconiq.Core.Models;

/// <summary>A status change reconstructed from the append-only purchase-order audit trail.</summary>
public sealed record PurchaseOrderStatusHistoryEntry(
    DateTime TimestampUtc,
    PurchaseOrderStatus PreviousStatus,
    PurchaseOrderStatus Status,
    string Actor);
