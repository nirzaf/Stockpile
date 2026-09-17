using FluentAssertions;
using Merconiq.Web.BackgroundServices;

namespace Merconiq.Tests.Web.Services;

public sealed class WebhookIdentityHeaderTests
{
    [Fact]
    public void Outbox_header_uses_the_stable_event_identity_without_exposing_a_delivery_row_id()
    {
        var eventId = Guid.Parse("429ed720-6b8e-44fb-91fa-270580d1fdf4");
        using var request = new HttpRequestMessage();

        WebhookDeliveryBackgroundService.AddEventIdentityHeader(request, eventId);

        request.Headers.GetValues("X-Inventory-Event-Id").Should().ContainSingle()
            .Which.Should().Be("429ed720-6b8e-44fb-91fa-270580d1fdf4");
        request.Headers.Contains("X-Inventory-Delivery-Id").Should().BeFalse();
    }
}
