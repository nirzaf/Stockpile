namespace Merconiq.Core.Services;

/// <summary>
/// Explicit commercial inputs for a sales-document line, after the calling workflow has
/// resolved its currency scale and effective tax treatment.
/// </summary>
public sealed record SalesDocumentLineAmount(
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPercent,
    decimal TaxRatePercent,
    TaxCalculationMode TaxMode,
    int CurrencyScale,
    TaxCategory TaxCategory,
    DocumentLineDirection Direction);

/// <summary>
/// Adapts sales-document line inputs to the shared versioned amount calculator.
/// This type does not resolve company currency, tax policy, or legal mappings.
/// </summary>
public static class SalesDocumentAmountAdapter
{
    /// <summary>Calculates one sales line using the shared rounding and calculation-version contract.</summary>
    public static CalculatedLineAmount CalculateLine(SalesDocumentLineAmount line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return DocumentAmountCalculator.Calculate(ToDocumentLineAmount(line));
    }

    /// <summary>
    /// Calculates document totals from independently rounded sales lines. The caller must
    /// provide the document scale explicitly; line-scale mismatches are rejected by the shared calculator.
    /// </summary>
    public static CalculatedDocumentAmount CalculateDocument(
        IEnumerable<SalesDocumentLineAmount> lines,
        int currencyScale)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return DocumentAmountCalculator.CalculateDocument(
            lines.Select(ToDocumentLineAmount),
            currencyScale);
    }

    private static DocumentLineAmount ToDocumentLineAmount(SalesDocumentLineAmount line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return new DocumentLineAmount(
            line.Quantity,
            line.UnitPrice,
            line.DiscountPercent,
            line.TaxRatePercent,
            line.TaxMode,
            line.CurrencyScale,
            line.TaxCategory,
            line.Direction);
    }
}
