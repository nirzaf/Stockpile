using Merconiq.Core.Models;

namespace Merconiq.Core.Interfaces;

/// <summary>Dispatches event notifications to all subscribed webhook endpoints.</summary>
public interface IWebhookDispatcher
{
    /// <summary>Records deliveries in the current business transaction.</summary>
    /// <param name="webhookEvent">The immutable event envelope.</param>
    /// <param name="cancellationToken">A token that cancels the subscription lookup.</param>
    /// <exception cref="WebhookPayloadTooLargeException">
    /// The serialized event envelope exceeds the configured UTF-8 byte limit.
    /// </exception>
    Task EnqueueAsync<T>(WebhookEvent<T> webhookEvent, CancellationToken cancellationToken = default);

    /// <summary>Sends an explicit tenant-aware event to matching active subscriptions.</summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="webhookEvent">The immutable event envelope.</param>
    /// <exception cref="WebhookPayloadTooLargeException">
    /// The serialized event envelope exceeds the configured UTF-8 byte limit.
    /// </exception>
    Task DispatchAsync<T>(WebhookEvent<T> webhookEvent);
}
