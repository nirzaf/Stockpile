using System.Security.Claims;
using System.Text.Json;
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
    public async Task Receive_WithoutIdempotencyKey_IsRejectedBeforePosting()
    {
        var mediator = new Mock<IMediator>();
        var authorization = new Mock<ICurrentUserAuthorization>();
        authorization.Setup(a => a.CanAccessLocationAsync(
                It.IsAny<ClaimsPrincipal>(), It.IsAny<int>(), CompanyCapability.Post))
            .ReturnsAsync(true);
        authorization.Setup(a => a.GetLocationCompanyIdAsync(
                It.IsAny<ClaimsPrincipal>(), It.IsAny<int>()))
            .ReturnsAsync(41);
        var store = new Mock<IIdempotencyKeyStore>();
        var controller = new StockController(mediator.Object, authorization.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.HttpContext.Request.Method = "POST";
        controller.HttpContext.Request.Path = "/api/v1/stock/receive";

        var result = await controller.Receive(
            new ReceiveStockCommand(7, 3, 2, "receive"),
            store.Object,
            new TestTenantContext("tenant-a"));

        result.Should().BeOfType<BadRequestObjectResult>();
        store.VerifyNoOtherCalls();
        mediator.Verify(m => m.Send(
            It.IsAny<ReceiveStockCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

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
        authorization.Setup(a => a.GetLocationCompanyIdAsync(
                It.IsAny<ClaimsPrincipal>(), It.IsAny<int>()))
            .ReturnsAsync(41);
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
        mediator.Verify(m => m.Send(
            It.Is<ReceiveStockCommand>(sent => sent.ItemId == command.ItemId &&
                sent.LocationId == command.LocationId &&
                sent.MutationScope.HasValue && sent.MutationScope.Value.CompanyId == 41 &&
                sent.MutationScope.Value.Reauthorize != null),
            cancellation.Token), Times.Once);
    }

    [Fact]
    public void ReceiveStockCommand_does_not_accept_company_scope_from_the_request_body()
    {
        const string json = """{"ItemId":7,"LocationId":3,"Quantity":2,"Notes":"receive","MutationScope":{"CompanyId":41}}""";

        var command = JsonSerializer.Deserialize<ReceiveStockCommand>(json);

        command.Should().NotBeNull();
        command!.MutationScope.Should().BeNull();
    }
}
