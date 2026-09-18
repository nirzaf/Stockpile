using Merconiq.Core.Models;

namespace Merconiq.Web.Components.Pages.PurchaseOrders;

internal static class PurchaseOrderObligationsPresentation
{
    internal const string ReceiptScopeNotice =
        "Posted receipt, acceptance, rejection, and outstanding quantities are not projected in this view.";

    internal static string GetOrderedSummary(PurchaseOrderLineObligations obligations) =>
        $"{obligations.OrderedQuantity} ordered units across {obligations.Lines.Count} lines.";
}
