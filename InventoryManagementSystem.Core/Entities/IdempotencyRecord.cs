using InventoryManagementSystem.Core.Interfaces;

namespace InventoryManagementSystem.Core.Entities;

/// <summary>Durable claim and result for an idempotent API request.</summary>
public sealed class IdempotencyRecord : ITenantScoped
{
    public long Id { get; set; }
    public string TenantId { get; set; } = "default";
    public string Scope { get; set; } = null!;
    public string Key { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public IdempotencyRecordStatus Status { get; set; } = IdempotencyRecordStatus.InProgress;
    public int AttemptCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int? ResponseStatusCode { get; set; }
    public string? ResponseBody { get; set; }
    public string? LastError { get; set; }
}

public enum IdempotencyRecordStatus
{
    InProgress,
    Completed,
    Failed
}
