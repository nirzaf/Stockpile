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
