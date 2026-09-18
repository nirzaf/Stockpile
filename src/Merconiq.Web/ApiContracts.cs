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

public sealed record UnitOfMeasureExportResponse(
    int PageSize,
    bool HasMore,
    string? NextCursor,
    IReadOnlyList<UnitOfMeasureExportRecord> Units);

public sealed record UnitOfMeasureExportRecord(
    string ExternalId,
    string Code,
    string Name,
    int DecimalPlaces,
    bool IsWholeUnitOnly);

public sealed record ItemMasterExportResponse(
    int PageSize,
    bool HasMore,
    string? NextCursor,
    IReadOnlyList<ItemMasterExportRecord> Items);

public sealed record ItemMasterExportRecord(
    string ExternalId,
    string ItemCode,
    string Description,
    string? BaseUnitExternalId,
    string? PurchaseUnitExternalId,
    string? SalesUnitExternalId,
    decimal PurchaseToBaseFactor,
    decimal SalesToBaseFactor,
    int QuantityPrecision,
    bool WholeUnitOnly,
    bool IsActive);
