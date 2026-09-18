using Merconiq.Core.Exceptions;

namespace Merconiq.Web.Components.Pages.PurchaseOrders;

internal enum PurchaseOrderStatusUpdateFailureKind
{
    ConcurrencyConflict,
    Rejected,
    Unconfirmed
}

internal static class PurchaseOrderStatusPresentation
{
    internal static PurchaseOrderStatusUpdateFailureKind GetFailureFeedback(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            ConcurrencyException => PurchaseOrderStatusUpdateFailureKind.ConcurrencyConflict,
            InvalidOperationException => PurchaseOrderStatusUpdateFailureKind.Rejected,
            _ => PurchaseOrderStatusUpdateFailureKind.Unconfirmed
        };
    }
}
