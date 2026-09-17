using Merconiq.Core.Entities;
using MediatR;

namespace Merconiq.Core.Features.Items.Commands;

public record CreateItemCommand(string ItemCode, string Description, decimal Rate, int? SupplierId,
    int? BaseUnitId = null, int? PurchaseUnitId = null, int? SalesUnitId = null,
    decimal PurchaseToBaseFactor = 1m, decimal SalesToBaseFactor = 1m,
    int QuantityPrecision = 0, bool WholeUnitOnly = false, string? Barcode = null) : IRequest<Item>;
