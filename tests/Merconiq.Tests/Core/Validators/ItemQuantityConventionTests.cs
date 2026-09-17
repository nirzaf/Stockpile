using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Features.Items.Commands;
using Merconiq.Core.Services;
using Merconiq.Core.Validators;
using FluentValidation.TestHelper;

namespace Merconiq.Tests.Core.Validators;

public sealed class ItemQuantityConventionTests
{
    [Fact]
    public void CreateItem_rejects_non_positive_conversion_factors()
    {
        var result = new CreateItemCommandValidator().TestValidate(
            new CreateItemCommand("SKU-1", "Widget", 10m, null, PurchaseToBaseFactor: 0m));

        result.ShouldHaveValidationErrorFor(command => command.PurchaseToBaseFactor);
    }

    [Fact]
    public void CreateItem_rejects_precision_outside_supported_range()
    {
        var result = new CreateItemCommandValidator().TestValidate(
            new CreateItemCommand("SKU-1", "Widget", 10m, null, QuantityPrecision: 7));

        result.ShouldHaveValidationErrorFor(command => command.QuantityPrecision);
    }

    [Fact]
    public void CreateItem_rejects_invalid_sales_factor_and_precision()
    {
        var validator = new CreateItemCommandValidator();

        validator.TestValidate(new CreateItemCommand("SKU-1", "Widget", 10m, null, SalesToBaseFactor: -1m))
            .ShouldHaveValidationErrorFor(command => command.SalesToBaseFactor);
        validator.TestValidate(new CreateItemCommand("SKU-1", "Widget", 10m, null, QuantityPrecision: -1))
            .ShouldHaveValidationErrorFor(command => command.QuantityPrecision);
    }

    [Fact]
    public void CreateItem_accepts_supported_quantity_conventions()
    {
        var result = new CreateItemCommandValidator().TestValidate(
            new CreateItemCommand("SKU-1", "Widget", 10m, null,
                PurchaseToBaseFactor: 1m, SalesToBaseFactor: 1m, QuantityPrecision: 6));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void UpdateItem_validates_nullable_quantity_conventions()
    {
        var validator = new UpdateItemCommandValidator();
        validator.TestValidate(new UpdateItemCommand(1, "Widget", 10m, null, SalesToBaseFactor: 0m))
            .ShouldHaveValidationErrorFor(command => command.SalesToBaseFactor);
        validator.TestValidate(new UpdateItemCommand(1, "Widget", 10m, null, QuantityPrecision: 7))
            .ShouldHaveValidationErrorFor(command => command.QuantityPrecision);
    }

    [Fact]
    public void Purchase_quantity_converts_to_base_without_rounding()
    {
        var item = new Item
        {
            PurchaseUnitId = 10,
            PurchaseToBaseFactor = 12m,
            QuantityPrecision = 0,
            WholeUnitOnly = true
        };

        ItemQuantityConventions.ToBaseQuantity(item, 2m, ItemQuantityUnit.Purchase)
            .Should().Be(24m);
    }

    [Fact]
    public void Whole_unit_only_item_rejects_fractional_quantity()
    {
        var item = new Item { QuantityPrecision = 0, WholeUnitOnly = true };

        var act = () => ItemQuantityConventions.ToBaseQuantity(item, 1.5m, ItemQuantityUnit.Base);

        act.Should().Throw<ArgumentException>()
            .WithMessage("Fractional quantities are not supported for whole-unit-only items.*");
    }

    [Fact]
    public void Conversion_rejects_a_result_that_would_need_silent_rounding()
    {
        var item = new Item
        {
            PurchaseUnitId = 10,
            PurchaseToBaseFactor = 1.333333m,
            QuantityPrecision = 2
        };

        var act = () => ItemQuantityConventions.ToBaseQuantity(item, 0.01m, ItemQuantityUnit.Purchase);

        act.Should().Throw<ArgumentException>()
            .WithMessage("Converted quantity must have no more than 2 decimal places.*");
    }
}
