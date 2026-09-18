using Merconiq.Core.Entities;

namespace Merconiq.Core.Models;

/// <summary>Derived line-obligation progress. This is not a goods-receipt or stock-posting status.</summary>
public enum PurchaseOrderProgressState
{
    NotStarted,
    PartiallyReceived,
    InspectionPending,
    AllReceivedAndClassified,
    LegacyReceivedWithoutLineProgress
}

/// <summary>Server-derived quantities for one ordered line.</summary>
public sealed record PurchaseOrderLineProgress(
    int LineId,
    int ItemId,
    string? ItemCode,
    string? ItemDescription,
    int OrderedQuantity,
    int ReceivedQuantity,
    int AcceptedQuantity,
    int RejectedQuantity,
    int OutstandingQuantity,
    int AwaitingInspectionQuantity);

/// <summary>Server-derived purchase-order lifecycle and aggregate line progress.</summary>
public sealed record PurchaseOrderReceivingProgress(
    int PurchaseOrderId,
    PurchaseOrderStatus LifecycleStatus,
    int Revision,
    PurchaseOrderProgressState ProgressState,
    long OrderedQuantity,
    long ReceivedQuantity,
    long AcceptedQuantity,
    long RejectedQuantity,
    long OutstandingQuantity,
    long AwaitingInspectionQuantity,
    IReadOnlyList<PurchaseOrderLineProgress> Lines);

/// <summary>Incremental line quantities submitted to the line-obligation API.</summary>
public sealed record PurchaseOrderLineProgressChange(
    int ReceivedQuantity,
    int AcceptedQuantity,
    int RejectedQuantity);
