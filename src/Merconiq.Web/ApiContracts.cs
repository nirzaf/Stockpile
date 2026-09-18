namespace Merconiq.Web;

public record TokenRequest(string Username, string Password);

public record WebhookSubscriptionRequest(string Url, string EventType, string? Secret, bool IsActive = true);

public record WebhookSubscriptionResponse(int Id, string Url, string EventType, bool IsActive);

public sealed record WebhookDeliveryDiagnosticResponse(
    long Id,
    Guid EventId,
    int SubscriptionId,
    string EventType,
    string Status,
    int AttemptCount,
    DateTimeOffset NextAttemptAt,
    DateTimeOffset? LastAttemptAt,
    int? LastStatusCode,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset CreatedAt,
    string? FailureReason);

public sealed record WebhookDeliveryDiagnosticsPageResponse(
    int Page,
    int PageSize,
    bool HasMore,
    IReadOnlyList<WebhookDeliveryDiagnosticResponse> Deliveries);
