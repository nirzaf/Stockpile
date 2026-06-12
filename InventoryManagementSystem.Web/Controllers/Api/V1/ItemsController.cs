using Asp.Versioning;
using InventoryManagementSystem.Core.Features.Items.Commands;
using InventoryManagementSystem.Core.Features.Items.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InventoryManagementSystem.Web.Controllers.Api.V1;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/items")]
[Produces("application/json")]
[Authorize]
public class ItemsController : ControllerBase
{
    private readonly IMediator _mediator;

    public ItemsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>Get all items</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<Core.Entities.Item>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll()
    {
        var items = await _mediator.Send(new GetAllItemsQuery());
        return Ok(items);
    }

    /// <summary>Get item by ID</summary>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(Core.Entities.Item), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(int id)
    {
        var item = await _mediator.Send(new GetItemByIdQuery(id));
        return item is null ? NotFound() : Ok(item);
    }

    /// <summary>Search items</summary>
    [HttpGet("search")]
    [ProducesResponseType(typeof(IEnumerable<Core.Entities.Item>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] string q)
    {
        var items = await _mediator.Send(new SearchItemsQuery(q));
        return Ok(items);
    }

    /// <summary>Create a new item</summary>
    [HttpPost]
    [ProducesResponseType(typeof(Core.Entities.Item), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreateItemCommand command)
    {
        var item = await _mediator.Send(command);
        return CreatedAtAction(nameof(GetById), new { id = item.Id }, item);
    }

    /// <summary>Update an existing item</summary>
    [HttpPut("{id:int}")]
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
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(int id)
    {
        await _mediator.Send(new DeleteItemCommand(id));
        return NoContent();
    }
}
