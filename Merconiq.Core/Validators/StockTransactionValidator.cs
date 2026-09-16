using FluentValidation;
using Merconiq.Core.Entities;

namespace Merconiq.Core.Validators;

public class StockTransactionValidator : AbstractValidator<StockTransaction>
{
    public StockTransactionValidator()
    {
        RuleFor(x => x.ItemId)
            .GreaterThan(0).WithMessage("Item is required");

        RuleFor(x => x.FromLocationId)
            .GreaterThan(0).WithMessage("Source location is required");

        RuleFor(x => x.Quantity)
            .GreaterThan(0).WithMessage("Quantity must be greater than zero");

        RuleFor(x => x.TransactionType)
            .IsInEnum().WithMessage("Invalid transaction type");

        RuleFor(x => x.Notes)
            .MaximumLength(500).WithMessage("Notes must not exceed 500 characters");

        RuleFor(x => x.ToLocationId)
            .GreaterThan(0).WithMessage("Destination location is required")
            .When(x => x.TransactionType == TransactionType.Transfer);
    }
}
