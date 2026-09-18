using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Models;
using Merconiq.Tests.Infrastructure;
using Merconiq.Web.Tenancy;
using Merconiq.Web.BackgroundServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

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
            claim.Status == nameof(WebhookDeliveryStatus.InProgress) &&
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
            delivery.Payload = new string('x', WebhookPayloadPolicy.MaximumSerializedEnvelopeBytes + 1);
            setup.WebhookDeliveries.Add(delivery);
            await setup.SaveChangesAsync();
            deliveryId = delivery.Id;
        }

        await using var expiredWorker = fixture.CreateContext(tenantId, "webhook-expired-worker");
        var firstClaim = await WebhookDeliveryLeaseStore.ClaimNextAsync(
            expiredWorker, now, TimeSpan.FromMinutes(2), CancellationToken.None);
        firstClaim.Should().NotBeNull();
        var firstLeaseToken = firstClaim!.LeaseToken!.Value;
        var firstClaimedDelivery = await WebhookDeliveryLeaseStore.FindOwnedAsync(
            expiredWorker, deliveryId, tenantId, firstLeaseToken, CancellationToken.None);
        firstClaimedDelivery.Should().NotBeNull();

        await using var recoveryWorker = fixture.CreateContext(tenantId, "webhook-recovery-worker");
        var recoveredClaim = await WebhookDeliveryLeaseStore.ClaimNextAsync(
            recoveryWorker, now.AddMinutes(3), TimeSpan.FromMinutes(2), CancellationToken.None);

        recoveredClaim.Should().NotBeNull();
        recoveredClaim!.Id.Should().Be(deliveryId);
        recoveredClaim.AttemptCount.Should().Be(2);
        recoveredClaim.LeaseToken.Should().NotBe(firstLeaseToken);
        recoveredClaim.LeaseUntil.Should().Be(now.AddMinutes(5));
        recoveredClaim.PayloadByteLength.Should().BeGreaterThan(WebhookPayloadPolicy.MaximumSerializedEnvelopeBytes);

        (await WebhookDeliveryLeaseStore.TryDeadLetterOversizedAsync(
            expiredWorker, firstClaim, now.AddMinutes(3), CancellationToken.None))
            .Should().BeFalse("a stale lease must not dead-letter a reclaimed delivery");

        firstClaimedDelivery!.Status = WebhookDeliveryStatus.Delivered;
        firstClaimedDelivery.DeliveredAt = now.AddMinutes(3);
        firstClaimedDelivery.LeaseUntil = null;
        firstClaimedDelivery.LeaseToken = null;

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

    [PostgreSqlFact]
    public async Task Worker_dead_letters_oversized_legacy_payload_without_selecting_or_sending_it()
    {
        fixture.EnsureEnabled();
        var tenantId = UniqueTenant();
        const string payloadMarker = "legacy-payload-private-marker";
        var oversizedPayload = $"{{\"payload\":\"{new string('é', WebhookPayloadPolicy.MaximumSerializedEnvelopeBytes / 2)}{payloadMarker}\"}}";
        Encoding.UTF8.GetByteCount(oversizedPayload)
            .Should().BeGreaterThan(WebhookPayloadPolicy.MaximumSerializedEnvelopeBytes);
        var commandCapture = new CommandCaptureInterceptor();
        var handler = new CountingHandler();
        long deliveryId;

        await using (var setup = fixture.CreateContext(tenantId))
        {
            var subscription = new WebhookSubscription
            {
                TenantId = tenantId,
                Url = "https://8.8.8.8/webhook",
                EventType = "Stock.Received"
            };
            setup.WebhookSubscriptions.Add(subscription);
            await setup.SaveChangesAsync();

            var delivery = NewDelivery(tenantId, PostgreSqlTimestampNow());
            delivery.SubscriptionId = subscription.Id;
            delivery.Payload = oversizedPayload;
            setup.WebhookDeliveries.Add(delivery);
            await setup.SaveChangesAsync();
            deliveryId = delivery.Id;
        }

        var services = new ServiceCollection();
        services.AddScoped<TenantContext>();
        services.AddScoped<Merconiq.Infrastructure.Data.InventoryDbContext>(
            _ => fixture.CreateContext(tenantId, null, commandCapture));
        services.AddHttpClient("Webhooks")
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider();
        var worker = new WebhookDeliveryBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IHttpClientFactory>(),
            NullLogger<WebhookDeliveryBackgroundService>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var delivery = await WaitForDeadLetterAsync(tenantId, deliveryId);

            handler.SendCount.Should().Be(0);
            delivery.LastError.Should().Be(WebhookPayloadPolicy.OversizedEnvelopeDiagnostic);
            delivery.LastError.Should().NotContain(payloadMarker);
            delivery.LastResponse.Should().BeNull();
            delivery.LastStatusCode.Should().BeNull();
            delivery.LeaseToken.Should().BeNull();
            delivery.LeaseUntil.Should().BeNull();

            var commands = commandCapture.Commands.ToArray();
            var claimProjection = commands.Single(command =>
                command.Contains("PayloadByteLength", StringComparison.Ordinal));
            claimProjection.Should().Contain("octet_length(convert_to(\"Payload\", 'UTF8'))");
            Regex.IsMatch(
                claimProjection,
                "(?:\\bSELECT|,)\\s*(?:[\\w\\\".]+\\.)?\\\"Payload\\\"(?:\\s|,)",
                RegexOptions.IgnoreCase)
                .Should().BeFalse("the metadata projection must not return the Payload column");

            var deadLetterUpdate = commands.Single(command =>
                command.Contains("UPDATE \"WebhookDeliveries\"", StringComparison.Ordinal) &&
                command.Contains("'DeadLetter'", StringComparison.Ordinal));
            var whereClause = deadLetterUpdate[deadLetterUpdate.LastIndexOf("WHERE", StringComparison.OrdinalIgnoreCase)..];
            whereClause.Should().Contain("\"Id\"");
            whereClause.Should().Contain("\"TenantId\"");
            whereClause.Should().Contain("\"Status\" = 'InProgress'");
            whereClause.Should().Contain("\"LeaseToken\"");
        }
        finally
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await worker.StopAsync(stopTimeout.Token);
        }
    }

    private async Task<WebhookDelivery> WaitForDeadLetterAsync(string tenantId, long deliveryId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            await using var verify = fixture.CreateContext(tenantId);
            var delivery = await verify.WebhookDeliveries.SingleAsync(item => item.Id == deliveryId);
            if (delivery.Status == WebhookDeliveryStatus.DeadLetter)
            {
                return delivery;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }
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

    private sealed class CommandCaptureInterceptor : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> _commands = new();

        public IReadOnlyCollection<string> Commands => _commands.ToArray();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _commands.Enqueue(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            _commands.Enqueue(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _sendCount;

        public int SendCount => Volatile.Read(ref _sendCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sendCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
