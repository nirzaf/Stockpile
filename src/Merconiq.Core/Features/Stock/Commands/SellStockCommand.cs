using MediatR;
using System.Text.Json.Serialization;
using Merconiq.Core.Models;

namespace Merconiq.Core.Features.Stock.Commands;

public record SellStockCommand(
    int ItemId,
    int LocationId,
    int Quantity,
    string? Notes,
    string? BatchNumber = null,
    DateTime? ExpiryDate = null,
    [property: JsonIgnore] StockMutationScope? MutationScope = null,
    string? ExpiryExceptionReason = null) : IRequest;
