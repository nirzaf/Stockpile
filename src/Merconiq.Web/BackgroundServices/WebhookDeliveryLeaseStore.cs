using System.Text;
using Merconiq.Core.Entities;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Web.BackgroundServices;

/// <summary>Claims one durable webhook delivery and fences updates from expired workers.</summary>
internal static class WebhookDeliveryLeaseStore
{
    private const string PostgreSqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    internal static async Task<WebhookDeliveryClaim?> ClaimNextAsync(
        InventoryDbContext db,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var leaseToken = Guid.NewGuid();
        var leaseUntil = now.Add(leaseDuration);

        if (!db.Database.IsRelational())
        {
            return await ClaimUsingTrackedEntitiesAsync(db, now, leaseUntil, leaseToken, cancellationToken);
        }

        EnsurePostgreSqlProvider(db);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH candidate AS (
                SELECT "Id"
                FROM "WebhookDeliveries"
                WHERE (("Status" = 'Pending' AND "NextAttemptAt" <= {now})
                    OR ("Status" = 'InProgress' AND "LeaseUntil" <= {now}))
                  AND NOT EXISTS (
                      SELECT 1
                      FROM "WebhookDeliveries" AS existing
                      WHERE existing."LeaseToken" = {leaseToken})
                ORDER BY "NextAttemptAt", "Id"
                LIMIT 1
                FOR UPDATE SKIP LOCKED
            )
            UPDATE "WebhookDeliveries" AS delivery
            SET "Status" = 'InProgress',
                "AttemptCount" = delivery."AttemptCount" + 1,
                "LastAttemptAt" = {now},
                "LeaseUntil" = {leaseUntil},
                "LeaseToken" = {leaseToken}
            FROM candidate
            WHERE delivery."Id" = candidate."Id"
            """, cancellationToken);

        return await db.Database.SqlQuery<WebhookDeliveryClaim>($"""
                SELECT "Id",
                       "TenantId",
                       "EventId",
                       "SubscriptionId",
                       "EventType",
                       "Status",
                       "AttemptCount",
                       "LeaseUntil",
                       "LeaseToken",
                       octet_length(convert_to("Payload", 'UTF8')) AS "PayloadByteLength"
                FROM "WebhookDeliveries"
                WHERE "LeaseToken" = {leaseToken}
                  AND "Status" = 'InProgress'
                """)
            .SingleOrDefaultAsync(cancellationToken);
    }

    internal static Task<WebhookDelivery?> FindOwnedAsync(
        InventoryDbContext db,
        long deliveryId,
        string tenantId,
        Guid leaseToken,
        CancellationToken cancellationToken) =>
        db.WebhookDeliveries
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(delivery =>
                delivery.Id == deliveryId &&
                delivery.TenantId == tenantId &&
                delivery.Status == WebhookDeliveryStatus.InProgress &&
                delivery.LeaseToken == leaseToken,
                cancellationToken);

    internal static async Task<WebhookDelivery?> FindOwnedWithinPayloadLimitAsync(
        InventoryDbContext db,
        long deliveryId,
        string tenantId,
        Guid leaseToken,
        int maximumPayloadBytes,
        CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational())
        {
            var delivery = await FindOwnedAsync(db, deliveryId, tenantId, leaseToken, cancellationToken);
            return delivery is not null &&
                Encoding.UTF8.GetByteCount(delivery.Payload) <= maximumPayloadBytes
                    ? delivery
                    : null;
        }

        EnsurePostgreSqlProvider(db);
        return await db.WebhookDeliveries
            .FromSqlInterpolated($"""
                SELECT *
                FROM "WebhookDeliveries"
                WHERE "Id" = {deliveryId}
                  AND "TenantId" = {tenantId}
                  AND "Status" = 'InProgress'
                  AND "LeaseToken" = {leaseToken}
                  AND octet_length(convert_to("Payload", 'UTF8')) <= {maximumPayloadBytes}
                """)
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(cancellationToken);
    }

    internal static async Task<bool> TryDeadLetterOversizedAsync(
        InventoryDbContext db,
        WebhookDeliveryClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!claim.LeaseToken.HasValue ||
            claim.PayloadByteLength <= WebhookPayloadPolicy.MaximumSerializedEnvelopeBytes)
        {
            return false;
        }

        if (!db.Database.IsRelational())
        {
            var delivery = await FindOwnedAsync(
                db,
                claim.Id,
                claim.TenantId,
                claim.LeaseToken.Value,
                cancellationToken);
            if (delivery is null)
            {
                return false;
            }

            delivery.Status = WebhookDeliveryStatus.DeadLetter;
            delivery.LastError = WebhookPayloadPolicy.OversizedEnvelopeDiagnostic;
            delivery.LastResponse = null;
            delivery.LastStatusCode = null;
            delivery.LastAttemptAt = now;
            delivery.LeaseUntil = null;
            delivery.LeaseToken = null;

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                return false;
            }
        }

        EnsurePostgreSqlProvider(db);
        var affectedRows = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "WebhookDeliveries"
            SET "Status" = 'DeadLetter',
                "LastError" = {WebhookPayloadPolicy.OversizedEnvelopeDiagnostic},
                "LastResponse" = NULL,
                "LastStatusCode" = NULL,
                "LastAttemptAt" = {now},
                "LeaseUntil" = NULL,
                "LeaseToken" = NULL
            WHERE "Id" = {claim.Id}
              AND "TenantId" = {claim.TenantId}
              AND "Status" = 'InProgress'
              AND "LeaseToken" = {claim.LeaseToken}
            """, cancellationToken);
        return affectedRows == 1;
    }

    internal static bool IsOwnedBy(WebhookDelivery delivery, Guid leaseToken) =>
        delivery.Status == WebhookDeliveryStatus.InProgress && delivery.LeaseToken == leaseToken;

    internal static bool IsOwnedBy(WebhookDeliveryClaim claim, Guid leaseToken) =>
        claim.Status == nameof(WebhookDeliveryStatus.InProgress) &&
        claim.LeaseToken == leaseToken &&
        leaseToken != Guid.Empty;

    private static async Task<WebhookDeliveryClaim?> ClaimUsingTrackedEntitiesAsync(
        InventoryDbContext db,
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        Guid leaseToken,
        CancellationToken cancellationToken)
    {
        // Non-relational providers are used only by fast tests and local demos. Production claims
        // must use PostgreSQL's atomic SKIP LOCKED statement above.
        var delivery = await db.WebhookDeliveries
            .IgnoreQueryFilters()
            .Where(item =>
                (item.Status == WebhookDeliveryStatus.Pending && item.NextAttemptAt <= now) ||
                (item.Status == WebhookDeliveryStatus.InProgress && item.LeaseUntil <= now))
            .OrderBy(item => item.NextAttemptAt)
            .ThenBy(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (delivery is null)
        {
            return null;
        }

        delivery.Status = WebhookDeliveryStatus.InProgress;
        delivery.AttemptCount++;
        delivery.LastAttemptAt = now;
        delivery.LeaseUntil = leaseUntil;
        delivery.LeaseToken = leaseToken;
        await db.SaveChangesAsync(cancellationToken);
        return new WebhookDeliveryClaim
        {
            Id = delivery.Id,
            TenantId = delivery.TenantId,
            EventId = delivery.EventId,
            SubscriptionId = delivery.SubscriptionId,
            EventType = delivery.EventType,
            Status = delivery.Status.ToString(),
            AttemptCount = delivery.AttemptCount,
            LeaseUntil = delivery.LeaseUntil,
            LeaseToken = delivery.LeaseToken,
            PayloadByteLength = Encoding.UTF8.GetByteCount(delivery.Payload)
        };
    }

    private static void EnsurePostgreSqlProvider(InventoryDbContext db)
    {
        if (!string.Equals(db.Database.ProviderName, PostgreSqlProviderName, StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"Atomic webhook delivery claims require PostgreSQL; provider '{db.Database.ProviderName}' is not supported.");
        }
    }
}

/// <summary>Bounded metadata returned when a delivery is claimed; it intentionally excludes the payload.</summary>
internal sealed class WebhookDeliveryClaim
{
    public long Id { get; set; }
    public string TenantId { get; set; } = null!;
    public Guid EventId { get; set; }
    public int SubscriptionId { get; set; }
    public string EventType { get; set; } = null!;
    public string Status { get; set; } = null!;
    public int AttemptCount { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public Guid? LeaseToken { get; set; }
    public int PayloadByteLength { get; set; }
}
