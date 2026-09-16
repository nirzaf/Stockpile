using FluentValidation;
using Merconiq.Core.Features.Items.Commands;
using Merconiq.Core.Features.Stock.Commands;

namespace Merconiq.Core.Validators;

public sealed class CreateItemCommandValidator : AbstractValidator<CreateItemCommand>
{
    public CreateItemCommandValidator()
    {
        RuleFor(command => command.ItemCode).NotEmpty().MaximumLength(50);
        RuleFor(command => command.Description).MaximumLength(500);
        RuleFor(command => command.Rate).GreaterThan(0);
        RuleFor(command => command.SupplierId).GreaterThan(0).When(command => command.SupplierId.HasValue);
        RuleFor(command => command.PurchaseToBaseFactor).GreaterThan(0);
        RuleFor(command => command.SalesToBaseFactor).GreaterThan(0);
        RuleFor(command => command.QuantityPrecision).InclusiveBetween(0, 6);
    }
}

public sealed class UpdateItemCommandValidator : AbstractValidator<UpdateItemCommand>
{
    public UpdateItemCommandValidator()
    {
        RuleFor(command => command.Id).GreaterThan(0);
        RuleFor(command => command.Description).MaximumLength(500);
        RuleFor(command => command.Rate).GreaterThan(0);
        RuleFor(command => command.SupplierId).GreaterThan(0).When(command => command.SupplierId.HasValue);
        RuleFor(command => command.PurchaseToBaseFactor).GreaterThan(0);
        RuleFor(command => command.SalesToBaseFactor).GreaterThan(0);
        RuleFor(command => command.QuantityPrecision).InclusiveBetween(0, 6);
    }
}

public sealed class DeleteItemCommandValidator : AbstractValidator<DeleteItemCommand>
{
    public DeleteItemCommandValidator()
    {
        RuleFor(command => command.Id).GreaterThan(0);
    }
}

public sealed class ReceiveStockCommandValidator : AbstractValidator<ReceiveStockCommand>
{
    public ReceiveStockCommandValidator()
    {
        RuleFor(command => command.ItemId).GreaterThan(0);
        RuleFor(command => command.LocationId).GreaterThan(0);
        RuleFor(command => command.Quantity).GreaterThan(0);
        RuleFor(command => command.Notes).MaximumLength(500);
        RuleFor(command => command.BatchNumber).MaximumLength(100);
    }
}

public sealed class TransferStockCommandValidator : AbstractValidator<TransferStockCommand>
{
    public TransferStockCommandValidator()
    {
        RuleFor(command => command.ItemId).GreaterThan(0);
        RuleFor(command => command.FromLocationId).GreaterThan(0);
        RuleFor(command => command.ToLocationId).GreaterThan(0);
        RuleFor(command => command.FromLocationId)
            .NotEqual(command => command.ToLocationId)
            .WithMessage("Source and destination must be different.");
        RuleFor(command => command.Quantity).GreaterThan(0);
        RuleFor(command => command.Notes).MaximumLength(500);
        RuleFor(command => command.BatchNumber).MaximumLength(100);
    }
}

public sealed class SellStockCommandValidator : AbstractValidator<SellStockCommand>
{
    public SellStockCommandValidator()
    {
        RuleFor(command => command.ItemId).GreaterThan(0);
        RuleFor(command => command.LocationId).GreaterThan(0);
        RuleFor(command => command.Quantity).GreaterThan(0);
        RuleFor(command => command.Notes).MaximumLength(500);
        RuleFor(command => command.BatchNumber).MaximumLength(100);
    }
}
