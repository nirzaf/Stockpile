using System.Security.Claims;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Features.Stock.Commands;
using Merconiq.Core.Interfaces;
using Merconiq.Tests.Infrastructure;
using Merconiq.Web.Controllers.Api.V1;
using Merconiq.Web.Security;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Merconiq.Tests.Web.Controllers;

public class SellStockIdempotencyTests
{
    [Fact]
    public async Task Sell_WithIdempotencyKey_UsesSharedCoordinator()
    {
        var mediator = new Mock<IMediator>();
        var store = new Mock<IIdempotencyKeyStore>();
        var controller = CreateController(mediator);
        controller.HttpContext.Request.Method = "POST";
        controller.HttpContext.Request.Path = "/api/v1/stock/sell";
        controller.HttpContext.Request.Headers["Idempotency-Key"] = "sale-1";
        using var cancellation = new CancellationTokenSource();
        controller.HttpContext.RequestAborted = cancellation.Token;

        var command = new SellStockCommand(7, 3, 2, "sale");
        var result = await controller.Sell(command, store.Object, new TestTenantContext("tenant-a"));

        result.Should().BeOfType<NoContentResult>();
        store.Verify(s => s.ExecuteAsync(
            "tenant-a:POST:/api/v1/stock/sell",
            "sale-1",
            It.IsAny<string>(),
            It.IsAny<Func<Task>>(),
            cancellation.Token), Times.Once);
        mediator.Verify(m => m.Send(It.IsAny<SellStockCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Sell_WithOversizedIdempotencyKey_ReturnsBadRequest()
    {
        var mediator = new Mock<IMediator>();
        var store = new Mock<IIdempotencyKeyStore>();
        var controller = CreateController(mediator);
        controller.HttpContext.Request.Headers["Idempotency-Key"] = new string('x', 201);

        var result = await controller.Sell(
            new SellStockCommand(7, 3, 2, null),
            store.Object,
            new TestTenantContext("tenant-a"));

        result.Should().BeOfType<BadRequestObjectResult>();
        store.VerifyNoOtherCalls();
    }

    private static StockController CreateController(Mock<IMediator> mediator)
    {
        var authorization = new Mock<ICurrentUserAuthorization>();
        authorization.Setup(a => a.CanAccessLocationAsync(
                It.IsAny<ClaimsPrincipal>(), It.IsAny<int>(), It.IsAny<CompanyCapability>()))
            .ReturnsAsync(true);
        return new StockController(mediator.Object, authorization.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }
}
