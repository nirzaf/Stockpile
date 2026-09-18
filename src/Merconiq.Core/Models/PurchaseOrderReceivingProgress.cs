using System.ComponentModel.DataAnnotations;
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
    [param: Range(0, int.MaxValue, ErrorMessage = "Receiving quantities must be non-negative.")]
    int ReceivedQuantity,
    [param: Range(0, int.MaxValue, ErrorMessage = "Receiving quantities must be non-negative.")]
    int AcceptedQuantity,
    [param: Range(0, int.MaxValue, ErrorMessage = "Receiving quantities must be non-negative.")]
    int RejectedQuantity) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ReceivedQuantity == 0 && AcceptedQuantity == 0 && RejectedQuantity == 0)
        {
            yield return new ValidationResult(
                "At least one non-negative receiving outcome quantity must be positive.",
                [nameof(ReceivedQuantity), nameof(AcceptedQuantity), nameof(RejectedQuantity)]);
        }
    }
}
