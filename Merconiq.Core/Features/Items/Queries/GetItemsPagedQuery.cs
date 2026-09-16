using Merconiq.Core.Entities;
using MediatR;

namespace Merconiq.Core.Features.Items.Queries;

public record GetItemsPagedQuery(int Page, int PageSize) : IRequest<ItemsPagedResult>;

public record ItemsPagedResult(IEnumerable<Item> Items, int TotalCount);
