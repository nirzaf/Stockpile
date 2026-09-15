using InventoryManagementSystem.Core.Models;

namespace InventoryManagementSystem.Core.Interfaces;

/// <summary>Dispatches event notifications to all subscribed webhook endpoints.</summary>
public interface IWebhookDispatcher
{
    /// <summary>Records deliveries in the current business transaction.</summary>
    Task EnqueueAsync<T>(WebhookEvent<T> webhookEvent);

    /// <summary>Sends an explicit tenant-aware event to matching active subscriptions.</summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="webhookEvent">The immutable event envelope.</param>
    Task DispatchAsync<T>(WebhookEvent<T> webhookEvent);
}
