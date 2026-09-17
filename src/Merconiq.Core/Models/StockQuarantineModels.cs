namespace Merconiq.Core.Models;

/// <summary>Request to quarantine available stock or release previously quarantined stock.</summary>
public sealed record ChangeStockQuarantineRequest(
    int ItemId,
    int LocationId,
    int Quantity,
    string SourceLineReference,
    string? BatchNumber,
    DateTime? ExpiryDate,
    string Reason);

/// <summary>Typed outbox payload for a quarantine or quarantine-release movement.</summary>
public sealed record StockQuarantineWebhookPayload(
    int ItemId,
    int LocationId,
    int Quantity,
    string SourceLineReference,
    string? BatchNumber,
    DateTime? ExpiryDate,
    string QuarantineReason);
