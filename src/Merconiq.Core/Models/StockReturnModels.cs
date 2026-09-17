using Merconiq.Core.Entities;

namespace Merconiq.Core.Models;

/// <summary>Request to return a bounded quantity against one original sale movement.</summary>
public sealed record CreateStockReturnRequest(
    int OriginalTransactionId,
    int Quantity,
    StockReturnDisposition Disposition,
    string SourceLineReference,
    string? Notes = null);
