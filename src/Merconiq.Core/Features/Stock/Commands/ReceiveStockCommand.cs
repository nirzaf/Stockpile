using MediatR;
using System.Text.Json.Serialization;
using Merconiq.Core.Models;

namespace Merconiq.Core.Features.Stock.Commands;

public record ReceiveStockCommand(
    int ItemId,
    int LocationId,
    int Quantity,
    string? Notes,
    string? BatchNumber = null,
    DateTime? ExpiryDate = null,
    decimal? UnitCost = null,
    [property: JsonIgnore] StockMutationScope? MutationScope = null) : IRequest;
