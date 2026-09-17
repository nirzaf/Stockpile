using Merconiq.Core.Entities;
using Merconiq.Core.Services;

namespace Merconiq.Core.Models;

/// <summary>Commercial changes requested against the exact version currently being edited.</summary>
public sealed record PurchaseOrderAmendment(
    int ExpectedCommercialVersion,
    int SupplierId,
    string? DeliveryTerms,
    string? Notes,
    int CurrencyScale,
    IReadOnlyCollection<PurchaseOrderAmendmentLine> Lines);

/// <summary>Commercial values for one existing, identity-preserving PO line.</summary>
public sealed record PurchaseOrderAmendmentLine(
    int Id,
    int ItemId,
    int Quantity,
    decimal UnitPrice,
    decimal DiscountPercent,
    int? TaxRuleId,
    decimal TaxRatePercent,
    TaxCategory TaxCategory,
    TaxCalculationMode TaxMode,
    DocumentLineDirection Direction);
