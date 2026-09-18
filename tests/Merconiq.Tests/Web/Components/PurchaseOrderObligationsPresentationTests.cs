using FluentAssertions;
using Merconiq.Core.Models;
using Merconiq.Web.Components.Pages.PurchaseOrders;

namespace Merconiq.Tests.Web.Components;

public sealed class PurchaseOrderObligationsPresentationTests
{
    [Fact]
    public void SummaryAndNoticeShowOnlyOrderedQuantitiesAndDoNotImplyReceiptProgress()
    {
        var obligations = new PurchaseOrderLineObligations(
            PurchaseOrderId: 42,
            OrderedQuantity: 12,
            Lines:
            [
                new PurchaseOrderLineObligation(1, 2, "ITEM-1", "First item", 5),
                new PurchaseOrderLineObligation(2, 3, "ITEM-2", "Second item", 7)
            ]);

        PurchaseOrderObligationsPresentation.GetOrderedSummary(obligations)
            .Should().Be("12 ordered units across 2 lines.");
        PurchaseOrderObligationsPresentation.ReceiptScopeNotice
            .Should().Contain("not projected")
            .And.NotContain("received ");
    }
}
