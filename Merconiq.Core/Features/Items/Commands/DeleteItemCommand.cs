using MediatR;

namespace Merconiq.Core.Features.Items.Commands;

public record DeleteItemCommand(int Id) : IRequest;
