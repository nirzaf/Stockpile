using FluentAssertions;
using Merconiq.Core.Entities;

namespace Merconiq.Tests.Core.Entities;

public sealed class TransferOrderTests
{
    [Fact]
    public void Reservation_reference_is_stable_until_an_approved_amendment()
    {
        var line = new TransferOrderLine();
        var initial = line.ReservationSourceLineReference;

        line.SetReservationVersionForAmendment();

        line.ReservationSourceLineReference.Should().NotBe(initial)
            .And.EndWith(":v2");
        line.ReservationSourceLineReference.Should().Contain(line.DocumentLineId.Value.ToString("N"));
    }
}
