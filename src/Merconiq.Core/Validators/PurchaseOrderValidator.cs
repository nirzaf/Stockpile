using FluentValidation;
using Merconiq.Core.Entities;

namespace Merconiq.Core.Validators;

public class PurchaseOrderValidator : AbstractValidator<PurchaseOrder>
{
    public PurchaseOrderValidator()
    {
        RuleFor(x => x.PONumber)
            .NotEmpty().WithMessage("PO number is required")
            .MaximumLength(50).WithMessage("PO number must not exceed 50 characters");

        RuleFor(x => x.SupplierId)
            .GreaterThan(0).WithMessage("Supplier is required");

        RuleFor(x => x.TotalAmount)
            .GreaterThanOrEqualTo(0).WithMessage("Total amount cannot be negative");

        RuleFor(x => x.Notes)
            .MaximumLength(500).WithMessage("Notes must not exceed 500 characters");
    }
}
