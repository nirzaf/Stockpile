using FluentAssertions;
using Merconiq.Core.Exceptions;
using Merconiq.Web.Components.Pages.PurchaseOrders;

namespace Merconiq.Tests.Web.Components;

public sealed class PurchaseOrderStatusPresentationTests
{
    [Fact]
    public void GetFailureFeedback_IdentifiesTypedConcurrencyConflicts()
    {
        PurchaseOrderStatusPresentation.GetFailureFeedback(new ConcurrencyException("conflict"))
            .Should().Be(PurchaseOrderStatusUpdateFailureKind.ConcurrencyConflict);
    }

    [Fact]
    public void GetFailureFeedback_TreatsInvalidTransitionsAsRejectedActions()
    {
        PurchaseOrderStatusPresentation.GetFailureFeedback(new InvalidOperationException("rejected"))
            .Should().Be(PurchaseOrderStatusUpdateFailureKind.Rejected);
    }

    [Fact]
    public void GetFailureFeedback_DoesNotAssumeTheOutcomeForUnexpectedFailures()
    {
        PurchaseOrderStatusPresentation.GetFailureFeedback(new IOException("unavailable"))
            .Should().Be(PurchaseOrderStatusUpdateFailureKind.Unconfirmed);
    }
}
