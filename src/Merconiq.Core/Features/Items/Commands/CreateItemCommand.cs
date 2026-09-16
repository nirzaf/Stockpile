using Merconiq.Core.Entities;
using MediatR;

namespace Merconiq.Core.Features.Items.Commands;

public record CreateItemCommand(string ItemCode, string Description, decimal Rate, int? SupplierId) : IRequest<Item>;
