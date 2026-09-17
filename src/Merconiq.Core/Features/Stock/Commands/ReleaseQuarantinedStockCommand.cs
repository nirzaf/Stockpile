using System.Text.Json.Serialization;
using MediatR;
using Merconiq.Core.Models;

namespace Merconiq.Core.Features.Stock.Commands;

public sealed record ReleaseQuarantinedStockCommand(
    int ItemId,
    int LocationId,
    int Quantity,
    string SourceLineReference,
    string? BatchNumber,
    DateTime? ExpiryDate,
    string Reason,
    [property: JsonIgnore] StockMutationScope? MutationScope = null) : IRequest;
