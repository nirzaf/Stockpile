namespace Merconiq.Core.Services;

public enum TaxCalculationMode
{
    Exclusive,
    Inclusive
}

public enum TaxCategory
{
    Standard,
    ZeroRated,
    Exempt
}

public enum DocumentLineDirection
{
    Charge,
    Reversal
}

public sealed record DocumentLineAmount(
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPercent = 0m,
    decimal TaxRatePercent = 0m,
    TaxCalculationMode TaxMode = TaxCalculationMode.Exclusive,
    int CurrencyScale = 2,
    TaxCategory TaxCategory = TaxCategory.Standard,
    DocumentLineDirection Direction = DocumentLineDirection.Charge);

public sealed record CalculatedLineAmount(
    decimal NetAmount,
    decimal DiscountAmount,
    decimal TaxableAmount,
    decimal TaxAmount,
    decimal GrossAmount,
    decimal TaxRatePercent,
    TaxCalculationMode TaxMode,
    TaxCategory TaxCategory,
    int CurrencyScale,
    DocumentLineDirection Direction,
    int CalculationVersion);

public sealed record CalculatedDocumentAmount(
    decimal NetAmount,
    decimal DiscountAmount,
    decimal TaxableAmount,
    decimal TaxAmount,
    decimal GrossAmount,
    int CurrencyScale,
    int CalculationVersion);

/// <summary>Calculates document amounts using decimal arithmetic and explicit currency rounding.</summary>
public static class DocumentAmountCalculator
{
    public const int CalculationVersion = 1;

    /// <summary>
    /// Calculates one line in this order: quantity × unit price, rounded base, discount,
    /// rounded taxable base, tax, and rounded gross amount. Inclusive tax is extracted from
    /// the rounded post-discount gross amount. Every intermediate monetary value is rounded
    /// using midpoint-away-from-zero at the requested currency scale.
    /// </summary>
    public static CalculatedLineAmount Calculate(DocumentLineAmount line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Quantity < 0) throw new ArgumentOutOfRangeException(nameof(line.Quantity));
        if (line.UnitPrice < 0) throw new ArgumentOutOfRangeException(nameof(line.UnitPrice));
        if (line.DiscountPercent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(line.DiscountPercent));
        if (line.TaxRatePercent < 0) throw new ArgumentOutOfRangeException(nameof(line.TaxRatePercent));
        if (line.CurrencyScale is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(line.CurrencyScale));
        if (!Enum.IsDefined(line.TaxMode)) throw new ArgumentOutOfRangeException(nameof(line.TaxMode));
        if (!Enum.IsDefined(line.TaxCategory)) throw new ArgumentOutOfRangeException(nameof(line.TaxCategory));
        if (!Enum.IsDefined(line.Direction)) throw new ArgumentOutOfRangeException(nameof(line.Direction));
        if (line.TaxCategory is TaxCategory.ZeroRated or TaxCategory.Exempt && line.TaxRatePercent != 0m)
        {
            throw new ArgumentException("Zero-rated and exempt lines must use a zero tax rate.", nameof(line));
        }

        var scale = line.CurrencyScale;
        var baseAmount = Round(line.Quantity * line.UnitPrice, scale);
        var discount = Round(baseAmount * line.DiscountPercent / 100m, scale);
        var taxable = Round(baseAmount - discount, scale);
        decimal tax;
        decimal gross;

        if (line.TaxMode == TaxCalculationMode.Inclusive)
        {
            gross = taxable;
            tax = line.TaxRatePercent == 0m
                ? 0m
                : Round(taxable - taxable / (1m + line.TaxRatePercent / 100m), scale);
            taxable = Round(gross - tax, scale);
        }
        else
        {
            tax = Round(taxable * line.TaxRatePercent / 100m, scale);
            gross = Round(taxable + tax, scale);
        }

        var sign = line.Direction == DocumentLineDirection.Reversal ? -1m : 1m;
        return new CalculatedLineAmount(
            sign * taxable,
            sign * discount,
            sign * taxable,
            sign * tax,
            sign * gross,
            line.TaxRatePercent,
            line.TaxMode,
            line.TaxCategory,
            scale,
            line.Direction,
            CalculationVersion);
    }

    /// <summary>Calculates and sums lines after each line has been independently rounded.</summary>
    public static CalculatedDocumentAmount CalculateDocument(
        IEnumerable<DocumentLineAmount> lines,
        int currencyScale = 2)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (currencyScale is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(currencyScale));

        var calculated = lines.Select(Calculate).ToArray();
        if (calculated.Any(line => line.CurrencyScale != currencyScale))
        {
            throw new ArgumentException("All document lines must use the document currency scale.", nameof(lines));
        }

        return new CalculatedDocumentAmount(
            calculated.Sum(line => line.NetAmount),
            calculated.Sum(line => line.DiscountAmount),
            calculated.Sum(line => line.TaxableAmount),
            calculated.Sum(line => line.TaxAmount),
            calculated.Sum(line => line.GrossAmount),
            currencyScale,
            CalculationVersion);
    }

    private static decimal Round(decimal value, int scale) =>
        decimal.Round(value, scale, MidpointRounding.AwayFromZero);
}
