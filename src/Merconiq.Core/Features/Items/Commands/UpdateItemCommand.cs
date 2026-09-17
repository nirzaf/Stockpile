using Merconiq.Core.Entities;
using MediatR;

namespace Merconiq.Core.Features.Items.Commands;

public record UpdateItemCommand(int Id, string Description, decimal Rate, int? SupplierId,
    int? BaseUnitId = null, int? PurchaseUnitId = null, int? SalesUnitId = null,
    decimal? PurchaseToBaseFactor = null, decimal? SalesToBaseFactor = null,
    int? QuantityPrecision = null, bool? WholeUnitOnly = null, bool? IsActive = null,
    string? Barcode = null) : IRequest;
