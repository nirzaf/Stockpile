using Merconiq.Core.Entities;

namespace Merconiq.Core.Models;

/// <summary>Current moving-average bucket and its immutable movement evidence.</summary>
public sealed record StockValuationView(
    int BucketId,
    int ItemId,
    int LocationId,
    int Quantity,
    decimal Value,
    IReadOnlyList<StockValuationEntryView> Entries);

/// <summary>One immutable valued stock movement.</summary>
public sealed record StockValuationEntryView(
    int Id,
    int StockTransactionId,
    StockValuationEntryType EntryType,
    int Quantity,
    decimal UnitCost,
    decimal TotalValue);
