using Merconiq.Core.Models;

namespace Merconiq.Web.Components.Pages.PurchaseOrders;

internal static class PurchaseOrderProgressPresentation
{
    internal static string GetStateLabel(PurchaseOrderProgressState state) => state switch
    {
        PurchaseOrderProgressState.NotStarted => "Not started",
        PurchaseOrderProgressState.PartiallyReceived => "Partially received",
        PurchaseOrderProgressState.InspectionPending => "Inspection pending",
        PurchaseOrderProgressState.AllReceivedAndClassified => "All received and classified",
        PurchaseOrderProgressState.LegacyReceivedWithoutLineProgress =>
            "Received lifecycle; legacy line quantities unavailable",
        _ => "Progress unavailable"
    };

    internal static double GetReceivedPercent(PurchaseOrderReceivingProgress progress) =>
        progress.OrderedQuantity <= 0
            ? 0
            : Math.Clamp(
                Math.Round(progress.ReceivedQuantity * 100d / progress.OrderedQuantity, 1),
                0,
                100);
}
