namespace Merconiq.Core.Services;

public enum TaxCalculationMode
{
    Exclusive,
    Inclusive
}

public sealed record DocumentLineAmount(
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPercent = 0m,
    decimal TaxRatePercent = 0m,
    TaxCalculationMode TaxMode = TaxCalculationMode.Exclusive,
    int CurrencyScale = 2);

public sealed record CalculatedLineAmount(
    decimal NetAmount,
    decimal DiscountAmount,
    decimal TaxableAmount,
    decimal TaxAmount,
    decimal GrossAmount,
    decimal TaxRatePercent,
    TaxCalculationMode TaxMode);

/// <summary>Calculates document amounts using decimal arithmetic and explicit currency rounding.</summary>
public static class DocumentAmountCalculator
{
    public static CalculatedLineAmount Calculate(DocumentLineAmount line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Quantity < 0) throw new ArgumentOutOfRangeException(nameof(line.Quantity));
        if (line.UnitPrice < 0) throw new ArgumentOutOfRangeException(nameof(line.UnitPrice));
        if (line.DiscountPercent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(line.DiscountPercent));
        if (line.TaxRatePercent < 0) throw new ArgumentOutOfRangeException(nameof(line.TaxRatePercent));
        if (line.CurrencyScale is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(line.CurrencyScale));

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

        return new CalculatedLineAmount(taxable, discount, taxable, tax, gross,
            line.TaxRatePercent, line.TaxMode);
    }

    private static decimal Round(decimal value, int scale) =>
        decimal.Round(value, scale, MidpointRounding.AwayFromZero);
}
