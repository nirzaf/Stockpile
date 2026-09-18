using FluentAssertions;
using Merconiq.Core.Services;

namespace Merconiq.Tests.Core.Services;

public sealed class DocumentAmountCalculatorTests
{
    public static TheoryData<DocumentLineAmount, decimal, decimal, decimal, decimal, decimal> SyntheticGoldenExamples => new()
    {
        // Independently calculated examples: round base, discount, taxable base, tax, then gross.
        {
            new DocumentLineAmount(3m, 19.995m, 12.5m, 7.5m),
            52.49m, 7.50m, 52.49m, 3.94m, 56.43m
        },
        {
            new DocumentLineAmount(2m, 57.50m, 10m, 15m, TaxCalculationMode.Inclusive),
            90.00m, 11.50m, 90.00m, 13.50m, 103.50m
        },
        {
            new DocumentLineAmount(1m, 10.49m, 10m, 10m, CurrencyScale: 0),
            9m, 1m, 9m, 1m, 10m
        },
        {
            new DocumentLineAmount(1m, 1.23456m, 12.5m, 7.5m, CurrencyScale: 4),
            1.0803m, 0.1543m, 1.0803m, 0.0810m, 1.1613m
        },
        {
            new DocumentLineAmount(2m, 1.005m, CurrencyScale: 2, TaxCategory: TaxCategory.ZeroRated),
            2.01m, 0m, 2.01m, 0m, 2.01m
        },
        {
            new DocumentLineAmount(3m, 0.3333m, 10m, CurrencyScale: 4, TaxCategory: TaxCategory.Exempt),
            0.8999m, 0.1000m, 0.8999m, 0m, 0.8999m
        }
    };

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
    [MemberData(nameof(SyntheticGoldenExamples))]
    public void SyntheticGoldenExamples_MatchRoundedLineSnapshots(
        DocumentLineAmount input,
        decimal expectedNet,
        decimal expectedDiscount,
        decimal expectedTaxable,
        decimal expectedTax,
        decimal expectedGross)
    {
        var result = DocumentAmountCalculator.Calculate(input);

        result.NetAmount.Should().Be(expectedNet);
        result.DiscountAmount.Should().Be(expectedDiscount);
        result.TaxableAmount.Should().Be(expectedTaxable);
        result.TaxAmount.Should().Be(expectedTax);
        result.GrossAmount.Should().Be(expectedGross);
        result.CurrencyScale.Should().Be(input.CurrencyScale);
        result.TaxMode.Should().Be(input.TaxMode);
        result.TaxCategory.Should().Be(input.TaxCategory);
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
