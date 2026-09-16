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

public class ReceiveStockIdempotencyTests
{
    [Fact]
    public async Task Receive_WithIdempotencyKey_ForwardsRequestCancellation()
    {
        var mediator = new Mock<IMediator>();
        mediator.Setup(m => m.Send(It.IsAny<ReceiveStockCommand>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var authorization = new Mock<ICurrentUserAuthorization>();
        authorization.Setup(a => a.CanAccessLocationAsync(
                It.IsAny<ClaimsPrincipal>(), It.IsAny<int>(), CompanyCapability.Post))
            .ReturnsAsync(true);
        var store = new Mock<IIdempotencyKeyStore>();
        Func<Task>? operation = null;
        CancellationToken observedToken = default;
        store
            .Setup(s => s.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<Func<Task>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string, Func<Task>, CancellationToken>((_, _, _, callback, token) =>
            {
                operation = callback;
                observedToken = token;
            })
            .Returns(Task.CompletedTask);

        var controller = new StockController(mediator.Object, authorization.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.HttpContext.Request.Method = "POST";
        controller.HttpContext.Request.Path = "/api/v1/stock/receive";
        controller.HttpContext.Request.Headers["Idempotency-Key"] = "receive-1";
        using var cancellation = new CancellationTokenSource();
        controller.HttpContext.RequestAborted = cancellation.Token;
        var command = new ReceiveStockCommand(7, 3, 2, "receive");

        var result = await controller.Receive(command, store.Object, new TestTenantContext("tenant-a"));
        await operation!();

        result.Should().BeOfType<NoContentResult>();
        observedToken.Should().Be(cancellation.Token);
        mediator.Verify(m => m.Send(command, cancellation.Token), Times.Once);
    }
}
