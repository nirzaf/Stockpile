namespace Merconiq.Core.Models;

/// <summary>Stable, versioned evidence of the commercial terms approved for a purchase order.</summary>
public sealed record PurchaseOrderCommercialSnapshot(
    int SchemaVersion,
    string TenantId,
    Guid DocumentId,
    string PONumber,
    int? CompanyId,
    int SupplierId,
    string SupplierName,
    string? SupplierAddress,
    string? SupplierEmail,
    string? DeliveryTerms,
    string? Notes,
    int CurrencyScale,
    int CalculationVersion,
    decimal NetAmount,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal TotalAmount,
    IReadOnlyList<PurchaseOrderCommercialLineSnapshot> Lines);

/// <summary>Item, UOM, quantity, pricing and tax facts captured for an approved PO line.</summary>
public sealed record PurchaseOrderCommercialLineSnapshot(
    Guid DocumentLineId,
    int ItemId,
    string ItemCode,
    string ItemDescription,
    int? UnitOfMeasureId,
    string? UnitOfMeasureCode,
    decimal PurchaseToBaseFactor,
    int Quantity,
    decimal UnitPrice,
    decimal DiscountPercent,
    decimal TaxRatePercent,
    string TaxCategory,
    string TaxMode,
    string Direction,
    int CalculationVersion,
    decimal NetAmount,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal GrossAmount);
