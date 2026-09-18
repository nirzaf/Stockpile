using FluentAssertions;
using Merconiq.Core.Services;

namespace Merconiq.Tests.Core.Services;

public sealed class SalesDocumentAmountAdapterTests
{
    [Fact]
    public void CalculateLine_UsesSharedInclusiveTaxAndDiscountContract()
    {
        var line = new SalesDocumentLineAmount(
            Quantity: 2m,
            UnitPrice: 57.50m,
            DiscountPercent: 10m,
            TaxRatePercent: 15m,
            TaxMode: TaxCalculationMode.Inclusive,
            CurrencyScale: 2,
            TaxCategory: TaxCategory.Standard,
            Direction: DocumentLineDirection.Charge);

        var result = SalesDocumentAmountAdapter.CalculateLine(line);

        result.Should().Be(new CalculatedLineAmount(
            NetAmount: 90.00m,
            DiscountAmount: 11.50m,
            TaxableAmount: 90.00m,
            TaxAmount: 13.50m,
            GrossAmount: 103.50m,
            TaxRatePercent: 15m,
            TaxMode: TaxCalculationMode.Inclusive,
            TaxCategory: TaxCategory.Standard,
            CurrencyScale: 2,
            Direction: DocumentLineDirection.Charge,
            CalculationVersion: DocumentAmountCalculator.CalculationVersion));
    }

    [Fact]
    public void CalculateDocument_SumsRoundedChargesAndReversals()
    {
        SalesDocumentLineAmount[] lines =
        [
            new(3m, 1.005m, 0m, 0m, TaxCalculationMode.Exclusive, 2, TaxCategory.ZeroRated,
                DocumentLineDirection.Charge),
            new(1m, 1.005m, 0m, 0m, TaxCalculationMode.Exclusive, 2, TaxCategory.ZeroRated,
                DocumentLineDirection.Reversal)
        ];

        var result = SalesDocumentAmountAdapter.CalculateDocument(lines, currencyScale: 2);

        result.Should().Be(new CalculatedDocumentAmount(
            NetAmount: 2.01m,
            DiscountAmount: 0m,
            TaxableAmount: 2.01m,
            TaxAmount: 0m,
            GrossAmount: 2.01m,
            CurrencyScale: 2,
            CalculationVersion: DocumentAmountCalculator.CalculationVersion));
    }
}
