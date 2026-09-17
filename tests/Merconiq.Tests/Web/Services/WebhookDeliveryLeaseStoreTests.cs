using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Infrastructure.Data;
using Merconiq.Tests.Infrastructure;
using Merconiq.Web.BackgroundServices;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Tests.Web.Services;

public sealed class WebhookDeliveryLeaseStoreTests
{
    [Fact]
    public async Task InMemory_claim_sets_a_lease_and_does_not_claim_it_again_before_expiry()
    {
        var tenantId = $"lease-test-{Guid.NewGuid():N}";
        var databaseName = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        long deliveryId;

        await using (var setup = CreateContext(databaseName, tenantId))
        {
            var delivery = NewDelivery(tenantId);
            setup.WebhookDeliveries.Add(delivery);
            await setup.SaveChangesAsync();
            deliveryId = delivery.Id;
        }

        await using var worker = CreateContext(databaseName, tenantId);
        var claim = await WebhookDeliveryLeaseStore.ClaimNextAsync(
            worker, now, TimeSpan.FromMinutes(2), CancellationToken.None);

        claim.Should().NotBeNull();
        claim!.Id.Should().Be(deliveryId);
        claim.Status.Should().Be(WebhookDeliveryStatus.InProgress);
        claim.AttemptCount.Should().Be(1);
        claim.LeaseUntil.Should().Be(now.AddMinutes(2));
        claim.LeaseToken.Should().NotBeNull();
        WebhookDeliveryLeaseStore.IsOwnedBy(claim, claim.LeaseToken!.Value).Should().BeTrue();

        await using var secondWorker = CreateContext(databaseName, tenantId);
        var duplicate = await WebhookDeliveryLeaseStore.ClaimNextAsync(
            secondWorker, now.AddMinutes(1), TimeSpan.FromMinutes(2), CancellationToken.None);

        duplicate.Should().BeNull();
    }

    [Fact]
    public async Task InMemory_claim_replaces_an_expired_lease_and_fences_the_previous_token()
    {
        var tenantId = $"lease-test-{Guid.NewGuid():N}";
        var databaseName = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var oldToken = Guid.NewGuid();
        long deliveryId;

        await using (var setup = CreateContext(databaseName, tenantId))
        {
            var delivery = NewDelivery(tenantId);
            delivery.Status = WebhookDeliveryStatus.InProgress;
            delivery.AttemptCount = 1;
            delivery.LeaseUntil = now.AddSeconds(-1);
            delivery.LeaseToken = oldToken;
            setup.WebhookDeliveries.Add(delivery);
            await setup.SaveChangesAsync();
            deliveryId = delivery.Id;
        }

        await using var recoveredWorker = CreateContext(databaseName, tenantId);
        var recovered = await WebhookDeliveryLeaseStore.ClaimNextAsync(
            recoveredWorker, now, TimeSpan.FromMinutes(2), CancellationToken.None);

        recovered.Should().NotBeNull();
        recovered!.Id.Should().Be(deliveryId);
        recovered.AttemptCount.Should().Be(2);
        recovered.LeaseToken.Should().NotBeNull().And.NotBe(oldToken);
        WebhookDeliveryLeaseStore.IsOwnedBy(recovered, oldToken).Should().BeFalse();
        WebhookDeliveryLeaseStore.IsOwnedBy(recovered, recovered.LeaseToken!.Value).Should().BeTrue();

        var oldOwnerLookup = await WebhookDeliveryLeaseStore.FindOwnedAsync(
            recoveredWorker, deliveryId, tenantId, oldToken, CancellationToken.None);
        oldOwnerLookup.Should().BeNull();
    }

    private static InventoryDbContext CreateContext(string databaseName, string tenantId) =>
        new(
            new DbContextOptionsBuilder<InventoryDbContext>()
                .UseInMemoryDatabase(databaseName)
                .Options,
            new TestTenantContext(tenantId));

    private static WebhookDelivery NewDelivery(string tenantId) => new()
    {
        TenantId = tenantId,
        EventId = Guid.NewGuid(),
        SubscriptionId = 1,
        EventType = "Stock.Received",
        Payload = "{}",
        NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        CreatedAt = DateTimeOffset.UtcNow
    };
}
