using Merconiq.Core.Entities;

namespace Merconiq.Core.Services;

public enum ItemQuantityUnit
{
    Base,
    Purchase,
    Sales
}

/// <summary>Validates item quantity metadata and converts supported units without rounding.</summary>
public static class ItemQuantityConventions
{
    public static void Validate(Item item)
    {
        if (item.PurchaseToBaseFactor <= 0)
            throw new ArgumentException("Purchase-to-base factor must be positive.", nameof(item));
        if (item.SalesToBaseFactor <= 0)
            throw new ArgumentException("Sales-to-base factor must be positive.", nameof(item));
        if (item.QuantityPrecision is < 0 or > 6)
            throw new ArgumentException("Quantity precision must be between 0 and 6.", nameof(item));
        if (item.WholeUnitOnly && item.QuantityPrecision != 0)
            throw new ArgumentException("Whole-unit-only items must use zero quantity precision.", nameof(item));
    }

    public static decimal ToBaseQuantity(Item item, decimal quantity, ItemQuantityUnit unit)
    {
        Validate(item);
        if (quantity <= 0)
            throw new ArgumentException("Quantity must be positive.", nameof(quantity));
        if (item.WholeUnitOnly && quantity != decimal.Truncate(quantity))
            throw new ArgumentException("Fractional quantities are not supported for whole-unit-only items.", nameof(quantity));
        if (decimal.Round(quantity, item.QuantityPrecision, MidpointRounding.ToEven) != quantity)
            throw new ArgumentException(
                $"Quantity must have no more than {item.QuantityPrecision} decimal places.", nameof(quantity));

        var factor = unit switch
        {
            ItemQuantityUnit.Base => 1m,
            ItemQuantityUnit.Purchase when item.PurchaseUnitId.HasValue => item.PurchaseToBaseFactor,
            ItemQuantityUnit.Sales when item.SalesUnitId.HasValue => item.SalesToBaseFactor,
            _ => throw new ArgumentException("The selected unit is not configured for this item.", nameof(unit))
        };
        var converted = quantity * factor;
        if (decimal.Round(converted, item.QuantityPrecision, MidpointRounding.ToEven) != converted)
            throw new ArgumentException(
                $"Converted quantity must have no more than {item.QuantityPrecision} decimal places.", nameof(quantity));
        return converted;
    }
}
