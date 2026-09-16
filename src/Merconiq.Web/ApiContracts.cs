namespace Merconiq.Web;

public record TokenRequest(string Username, string Password);

public record WebhookSubscriptionRequest(string Url, string EventType, string? Secret, bool IsActive = true);

public record WebhookSubscriptionResponse(int Id, string Url, string EventType, bool IsActive);
