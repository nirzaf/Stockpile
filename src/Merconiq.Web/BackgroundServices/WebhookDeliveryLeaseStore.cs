using Merconiq.Core.Entities;
using Merconiq.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Web.BackgroundServices;

/// <summary>Claims one durable webhook delivery and fences updates from expired workers.</summary>
internal static class WebhookDeliveryLeaseStore
{
    private const string PostgreSqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    internal static async Task<WebhookDelivery?> ClaimNextAsync(
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

        if (!string.Equals(db.Database.ProviderName, PostgreSqlProviderName, StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"Atomic webhook delivery claims require PostgreSQL; provider '{db.Database.ProviderName}' is not supported.");
        }

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

        return await db.WebhookDeliveries
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(delivery => delivery.LeaseToken == leaseToken, cancellationToken);
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

    internal static bool IsOwnedBy(WebhookDelivery delivery, Guid leaseToken) =>
        delivery.Status == WebhookDeliveryStatus.InProgress && delivery.LeaseToken == leaseToken;

    private static async Task<WebhookDelivery?> ClaimUsingTrackedEntitiesAsync(
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
        return delivery;
    }
}
