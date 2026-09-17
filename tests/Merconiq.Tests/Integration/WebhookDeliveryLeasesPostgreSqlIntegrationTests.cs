using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Tests.Infrastructure;
using Merconiq.Web.BackgroundServices;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class WebhookDeliveryLeasesPostgreSqlIntegrationTests(PostgreSqlIntegrationFixture fixture)
{
    [PostgreSqlFact]
    public async Task Concurrent_workers_claim_each_delivery_once()
    {
        fixture.EnsureEnabled();
        const int workerCount = 8;
        var tenantId = UniqueTenant();
        var now = PostgreSqlTimestampNow();

        await using (var setup = fixture.CreateContext(tenantId))
        {
            setup.WebhookDeliveries.AddRange(Enumerable.Range(0, workerCount).Select(_ => NewDelivery(tenantId, now)));
            await setup.SaveChangesAsync();
        }

        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = Enumerable.Range(0, workerCount).Select(async workerNumber =>
        {
            await using var worker = fixture.CreateContext(tenantId, $"webhook-claim-{workerNumber}");
            await startGate.Task;
            return await WebhookDeliveryLeaseStore.ClaimNextAsync(
                worker, now, TimeSpan.FromMinutes(2), CancellationToken.None);
        }).ToArray();

        startGate.SetResult();
        var claims = await Task.WhenAll(workers);
        var claimed = claims.Where(claim => claim is not null).Select(claim => claim!).ToArray();

        claimed.Should().HaveCount(workerCount);
        claimed.Select(claim => claim.Id).Should().OnlyHaveUniqueItems();
        claimed.Select(claim => claim.LeaseToken).Should().OnlyHaveUniqueItems();
        claimed.Should().OnlyContain(claim =>
            claim.Status == WebhookDeliveryStatus.InProgress &&
            claim.AttemptCount == 1 &&
            claim.LeaseUntil == now.AddMinutes(2) &&
            claim.LeaseToken.HasValue);

        await using var verify = fixture.CreateContext(tenantId);
        var persisted = await verify.WebhookDeliveries.IgnoreQueryFilters()
            .Where(delivery => delivery.TenantId == tenantId)
            .ToListAsync();
        persisted.Should().HaveCount(workerCount);
        persisted.Select(delivery => delivery.Id).Should().OnlyHaveUniqueItems();
        persisted.Should().OnlyContain(delivery =>
            delivery.Status == WebhookDeliveryStatus.InProgress && delivery.AttemptCount == 1);
    }

    [PostgreSqlFact]
    public async Task Expired_worker_lease_is_reclaimed_and_old_worker_cannot_overwrite_it()
    {
        fixture.EnsureEnabled();
        var tenantId = UniqueTenant();
        var now = PostgreSqlTimestampNow();
        long deliveryId;

        await using (var setup = fixture.CreateContext(tenantId))
        {
            var delivery = NewDelivery(tenantId, now);
            setup.WebhookDeliveries.Add(delivery);
            await setup.SaveChangesAsync();
            deliveryId = delivery.Id;
        }

        await using var expiredWorker = fixture.CreateContext(tenantId, "webhook-expired-worker");
        var firstClaim = await WebhookDeliveryLeaseStore.ClaimNextAsync(
            expiredWorker, now, TimeSpan.FromMinutes(2), CancellationToken.None);
        firstClaim.Should().NotBeNull();
        var firstLeaseToken = firstClaim!.LeaseToken!.Value;

        await using var recoveryWorker = fixture.CreateContext(tenantId, "webhook-recovery-worker");
        var recoveredClaim = await WebhookDeliveryLeaseStore.ClaimNextAsync(
            recoveryWorker, now.AddMinutes(3), TimeSpan.FromMinutes(2), CancellationToken.None);

        recoveredClaim.Should().NotBeNull();
        recoveredClaim!.Id.Should().Be(deliveryId);
        recoveredClaim.AttemptCount.Should().Be(2);
        recoveredClaim.LeaseToken.Should().NotBe(firstLeaseToken);
        recoveredClaim.LeaseUntil.Should().Be(now.AddMinutes(5));

        firstClaim.Status = WebhookDeliveryStatus.Delivered;
        firstClaim.DeliveredAt = now.AddMinutes(3);
        firstClaim.LeaseUntil = null;
        firstClaim.LeaseToken = null;

        var staleCompletion = () => expiredWorker.SaveChangesAsync();
        await staleCompletion.Should().ThrowAsync<DbUpdateConcurrencyException>();

        await using var verify = fixture.CreateContext(tenantId);
        var persisted = await verify.WebhookDeliveries.IgnoreQueryFilters()
            .SingleAsync(delivery => delivery.Id == deliveryId && delivery.TenantId == tenantId);
        persisted.Status.Should().Be(WebhookDeliveryStatus.InProgress);
        persisted.AttemptCount.Should().Be(2);
        persisted.LeaseToken.Should().Be(recoveredClaim.LeaseToken);
        persisted.LeaseUntil.Should().Be(now.AddMinutes(5));

        (await WebhookDeliveryLeaseStore.FindOwnedAsync(
            verify, deliveryId, tenantId, firstLeaseToken, CancellationToken.None)).Should().BeNull();
        (await WebhookDeliveryLeaseStore.FindOwnedAsync(
            verify, deliveryId, tenantId, recoveredClaim.LeaseToken!.Value, CancellationToken.None))
            .Should().NotBeNull();
    }

    private static WebhookDelivery NewDelivery(string tenantId, DateTimeOffset now) => new()
    {
        TenantId = tenantId,
        EventId = Guid.NewGuid(),
        SubscriptionId = 1,
        EventType = "Stock.Received",
        Payload = "{}",
        NextAttemptAt = now.AddMinutes(-1),
        CreatedAt = now
    };

    private static string UniqueTenant() => $"webhook-lease-{Guid.NewGuid():N}";

    private static DateTimeOffset PostgreSqlTimestampNow()
    {
        var utcTicks = DateTime.UtcNow.Ticks;
        return new DateTimeOffset(utcTicks - (utcTicks % 10), TimeSpan.Zero);
    }
}
