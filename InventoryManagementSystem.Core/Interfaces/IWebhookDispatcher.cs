using System.Threading.Tasks;

namespace InventoryManagementSystem.Core.Interfaces;

/// <summary>Dispatches event notifications to all subscribed webhook endpoints.</summary>
public interface IWebhookDispatcher
{
    /// <summary>Sends a typed payload to every active webhook subscription matching the event type.</summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="eventType">A stable event name (e.g. <c>stock.received</c>).</param>
    /// <param name="payload">The serializable payload to send.</param>
    Task DispatchAsync<T>(string eventType, T payload);
}
