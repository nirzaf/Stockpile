using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Models;
using Merconiq.Web.Components.Pages.PurchaseOrders;

namespace Merconiq.Tests.Web.Components;

public sealed class PurchaseOrderProgressPresentationTests
{
    [Theory]
    [InlineData(PurchaseOrderProgressState.NotStarted, "Not started")]
    [InlineData(PurchaseOrderProgressState.PartiallyReceived, "Partially received")]
    [InlineData(PurchaseOrderProgressState.InspectionPending, "Inspection pending")]
    [InlineData(PurchaseOrderProgressState.AllReceivedAndClassified, "All received and classified")]
    public void GetStateLabel_ProvidesTextThatDoesNotRelyOnColor(
        PurchaseOrderProgressState state,
        string expectedLabel)
    {
        PurchaseOrderProgressPresentation.GetStateLabel(state).Should().Be(expectedLabel);
    }

    [Fact]
    public void GetReceivedPercent_ClampsAndRoundsTheServerProgressForTheAccessibleBar()
    {
        var progress = new PurchaseOrderReceivingProgress(
            9,
            PurchaseOrderStatus.Approved,
            1,
            PurchaseOrderProgressState.PartiallyReceived,
            OrderedQuantity: 7,
            ReceivedQuantity: 3,
            AcceptedQuantity: 1,
            RejectedQuantity: 1,
            OutstandingQuantity: 4,
            AwaitingInspectionQuantity: 1,
            Lines: []);

        PurchaseOrderProgressPresentation.GetReceivedPercent(progress).Should().Be(42.9);
    }

    [Fact]
    public void GetReceivedPercent_WithNoOrderedUnitsReturnsZero()
    {
        var progress = new PurchaseOrderReceivingProgress(
            9,
            PurchaseOrderStatus.Pending,
            0,
            PurchaseOrderProgressState.NotStarted,
            0, 0, 0, 0, 0, 0,
            []);

        PurchaseOrderProgressPresentation.GetReceivedPercent(progress).Should().Be(0);
    }
}
