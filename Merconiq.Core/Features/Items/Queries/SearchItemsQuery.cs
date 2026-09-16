using Merconiq.Core.Entities;
using MediatR;

namespace Merconiq.Core.Features.Items.Queries;

public record SearchItemsQuery(string SearchTerm) : IRequest<IEnumerable<Item>>;
