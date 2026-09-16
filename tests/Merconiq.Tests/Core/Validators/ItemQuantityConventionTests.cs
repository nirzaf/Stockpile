using FluentAssertions;
using Merconiq.Core.Features.Items.Commands;
using Merconiq.Core.Validators;

namespace Merconiq.Tests.Core.Validators;

public sealed class ItemQuantityConventionTests
{
    [Fact]
    public void CreateItem_rejects_non_positive_conversion_factors()
    {
        var result = new CreateItemCommandValidator().Validate(
            new CreateItemCommand("SKU-1", "Widget", 10m, null, PurchaseToBaseFactor: 0m));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == nameof(CreateItemCommand.PurchaseToBaseFactor));
    }

    [Fact]
    public void CreateItem_rejects_precision_outside_supported_range()
    {
        var result = new CreateItemCommandValidator().Validate(
            new CreateItemCommand("SKU-1", "Widget", 10m, null, QuantityPrecision: 7));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == nameof(CreateItemCommand.QuantityPrecision));
    }
}
