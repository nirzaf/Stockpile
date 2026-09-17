using FluentAssertions;
using Merconiq.Core.Services;

namespace Merconiq.Tests.Core.Services;

public sealed class DocumentAmountCalculatorTests
{
    [Fact]
    public void ExclusiveTax_DiscountIsAppliedBeforeTax()
    {
        var result = DocumentAmountCalculator.Calculate(new DocumentLineAmount(3, 19.99m, 10, 15));

        result.DiscountAmount.Should().Be(6.00m);
        result.TaxableAmount.Should().Be(53.97m);
        result.TaxAmount.Should().Be(8.10m);
        result.GrossAmount.Should().Be(62.07m);
    }

    [Fact]
    public void InclusiveTax_ExtractsTaxFromTheRoundedGrossAmount()
    {
        var result = DocumentAmountCalculator.Calculate(new DocumentLineAmount(
            1, 115m, TaxRatePercent: 15, TaxMode: TaxCalculationMode.Inclusive));

        result.TaxableAmount.Should().Be(100m);
        result.TaxAmount.Should().Be(15m);
        result.GrossAmount.Should().Be(115m);
    }

    [Fact]
    public void ZeroTax_IsSupported()
    {
        var result = DocumentAmountCalculator.Calculate(new DocumentLineAmount(2, 10.005m, CurrencyScale: 2));

        result.TaxAmount.Should().Be(0m);
        result.GrossAmount.Should().Be(20.01m);
    }

    [Fact]
    public void ExemptTax_RejectsNonZeroRate()
    {
        var action = () => DocumentAmountCalculator.Calculate(new DocumentLineAmount(
            1, 10m, TaxRatePercent: 5m, TaxCategory: TaxCategory.Exempt));

        action.Should().Throw<ArgumentException>()
            .WithMessage("Zero-rated and exempt lines must use a zero tax rate.*");
    }

    [Fact]
    public void Reversal_ReturnsSignedRoundedAmounts()
    {
        var result = DocumentAmountCalculator.Calculate(new DocumentLineAmount(
            2, 10m, TaxRatePercent: 15m, Direction: DocumentLineDirection.Reversal));

        result.NetAmount.Should().Be(-20m);
        result.TaxAmount.Should().Be(-3m);
        result.GrossAmount.Should().Be(-23m);
        result.Direction.Should().Be(DocumentLineDirection.Reversal);
    }

    [Fact]
    public void DocumentTotals_SumIndependentlyRoundedLines()
    {
        var result = DocumentAmountCalculator.CalculateDocument(
        [
            new DocumentLineAmount(1, 10.005m),
            new DocumentLineAmount(1, 0.005m)
        ]);

        result.NetAmount.Should().Be(10.02m);
        result.GrossAmount.Should().Be(10.02m);
        result.CalculationVersion.Should().Be(DocumentAmountCalculator.CalculationVersion);
    }

    [Theory]
    [InlineData(-1, 10, 0, 0)]
    [InlineData(1, 10, 101, 0)]
    [InlineData(1, 10, 0, -1)]
    public void InvalidInputs_AreRejected(decimal quantity, decimal price, decimal discount, decimal tax)
    {
        var action = () => DocumentAmountCalculator.Calculate(new DocumentLineAmount(quantity, price, discount, tax));

        action.Should().Throw<ArgumentOutOfRangeException>();
    }
}
