using Merconiq.Core.Entities;
using MediatR;

namespace Merconiq.Core.Features.Items.Commands;

public record UpdateItemCommand(int Id, string Description, decimal Rate, int? SupplierId) : IRequest;
