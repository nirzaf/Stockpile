namespace Merconiq.Core.Models;

/// <summary>
/// Captures the company that was authorized for a user-facing stock mutation.
/// The stock service compares it with current location ownership after acquiring
/// the location lock, preventing reassignment between authorization and posting.
/// The caller supplies a reauthorization callback, which is invoked after the
/// location lock is held. A null company id represents an authorized tenant-admin
/// operation on an unassigned legacy location.
/// </summary>
public readonly record struct StockMutationScope(
    int? CompanyId,
    Func<Task<bool>>? Reauthorize = null,
    Func<Task<bool>>? ReauthorizeExpiredStockOverride = null);
