using Asp.Versioning;
using Merconiq.Core.Entities;
using Merconiq.Core.Features.Items.Commands;
using Merconiq.Core.Features.Items.Queries;
using Merconiq.Core.Models;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Merconiq.Web.Security;

namespace Merconiq.Web.Controllers.Api.V1;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/items")]
[Produces("application/json")]
[Authorize(Policy = "Api")]
[EnableRateLimiting("Api")]
public class ItemsController : ControllerBase
{
    private readonly IMediator _mediator;

    public ItemsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>Get all items</summary>
    [HttpGet]
    [Authorize(Policy = CapabilityPolicies.View)]
    [ProducesResponseType(typeof(ApiResponse<ItemsPagedResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetAll([FromQuery] int page = 1, [FromQuery] int pageSize = 25)
    {
        if (page < 1 || pageSize < 1 || pageSize > 100)
            return BadRequest("page must be positive and pageSize must be between 1 and 100");

        var items = await _mediator.Send(new GetItemsPagedQuery(page, pageSize));
        return Ok(ApiResponse<ItemsPagedResult>.CreateSuccess(items));
    }

    /// <summary>Get item by ID</summary>
    [HttpGet("{id:int}")]
    [Authorize(Policy = CapabilityPolicies.View)]
    [ProducesResponseType(typeof(Core.Entities.Item), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(int id)
    {
        var item = await _mediator.Send(new GetItemByIdQuery(id));
        return item is null
            ? NotFound(ApiResponse<Item>.CreateFailure("Item not found"))
            : Ok(ApiResponse<Item>.CreateSuccess(item));
    }

    /// <summary>Search items</summary>
    [HttpGet("search")]
    [Authorize(Policy = CapabilityPolicies.View)]
    [ProducesResponseType(typeof(IEnumerable<Core.Entities.Item>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] string q)
    {
        var items = await _mediator.Send(new SearchItemsQuery(q));
        return Ok(ApiResponse<IEnumerable<Core.Entities.Item>>.CreateSuccess(items));
    }

    /// <summary>Create a new item</summary>
    [HttpPost]
    [Authorize(Policy = CapabilityPolicies.Edit)]
    [ProducesResponseType(typeof(Core.Entities.Item), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreateItemCommand command)
    {
        var item = await _mediator.Send(command);
        return CreatedAtAction(nameof(GetById), new { id = item.Id }, ApiResponse<Core.Entities.Item>.CreateSuccess(item));
    }

    /// <summary>Update an existing item</summary>
    [HttpPut("{id:int}")]
    [Authorize(Policy = CapabilityPolicies.Edit)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateItemCommand command)
    {
        if (id != command.Id)
            return BadRequest("Route ID does not match body ID");

        await _mediator.Send(command);
        return NoContent();
    }

    /// <summary>Delete an item</summary>
    [HttpDelete("{id:int}")]
    [Authorize(Policy = CapabilityPolicies.Edit)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(int id)
    {
        await _mediator.Send(new DeleteItemCommand(id));
        return NoContent();
    }
}
