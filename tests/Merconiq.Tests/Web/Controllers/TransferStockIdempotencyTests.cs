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

public class TransferStockIdempotencyTests
{
    [Fact]
    public async Task Transfer_WithIdempotencyKey_UsesSharedCoordinator()
    {
        var mediator = new Mock<IMediator>();
        var store = new Mock<IIdempotencyKeyStore>();
        var authorization = new Mock<ICurrentUserAuthorization>();
        authorization.Setup(a => a.CanAccessTransferAsync(
                It.IsAny<ClaimsPrincipal>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CompanyCapability>()))
            .ReturnsAsync(true);
        var controller = new StockController(mediator.Object, authorization.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.HttpContext.Request.Method = "POST";
        controller.HttpContext.Request.Path = "/api/v1/stock/transfer";
        controller.HttpContext.Request.Headers["Idempotency-Key"] = "transfer-1";
        using var cancellation = new CancellationTokenSource();
        controller.HttpContext.RequestAborted = cancellation.Token;

        var command = new TransferStockCommand(7, 3, 4, 2, "transfer");
        var result = await controller.Transfer(command, store.Object, new TestTenantContext("tenant-a"));

        result.Should().BeOfType<NoContentResult>();
        store.Verify(s => s.ExecuteAsync(
            "tenant-a:POST:/api/v1/stock/transfer",
            "transfer-1",
            It.IsAny<string>(),
            It.IsAny<Func<Task>>(),
            cancellation.Token), Times.Once);
        mediator.Verify(m => m.Send(It.IsAny<TransferStockCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Transfer_WithoutIdempotencyKey_PreservesExistingPath()
    {
        var mediator = new Mock<IMediator>();
        mediator.Setup(m => m.Send(It.IsAny<TransferStockCommand>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var store = new Mock<IIdempotencyKeyStore>();
        var authorization = new Mock<ICurrentUserAuthorization>();
        authorization.Setup(a => a.CanAccessTransferAsync(
                It.IsAny<ClaimsPrincipal>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CompanyCapability>()))
            .ReturnsAsync(true);
        var controller = new StockController(mediator.Object, authorization.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.HttpContext.Request.Method = "POST";
        controller.HttpContext.Request.Path = "/api/v1/stock/transfer";

        var result = await controller.Transfer(
            new TransferStockCommand(7, 3, 4, 2, null),
            store.Object,
            new TestTenantContext("tenant-a"));

        result.Should().BeOfType<NoContentResult>();
        mediator.Verify(m => m.Send(It.IsAny<TransferStockCommand>(), It.IsAny<CancellationToken>()), Times.Once);
        store.VerifyNoOtherCalls();
    }
}
