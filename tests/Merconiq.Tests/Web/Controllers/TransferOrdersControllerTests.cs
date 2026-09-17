using System.Security.Claims;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Tests.Infrastructure;
using Merconiq.Web.Controllers.Api.V1;
using Merconiq.Web.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Merconiq.Tests.Web.Controllers;

public sealed class TransferOrdersControllerTests
{
    [Fact]
    public async Task Dispatch_requires_post_access_and_a_keyed_command()
    {
        var order = new TransferOrderView(
            7, Guid.NewGuid(), "TO-2026-00001", 41, 3, 4, DateTime.UtcNow,
            TransferOrderStatus.Approved, null,
            [new TransferOrderLineView(12, Guid.NewGuid(), 5, 50, null, null, "TransferOrder:line:v1")]);
        var transfers = new Mock<ITransferOrderService>();
        transfers.Setup(service => service.GetByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(order);
        var authorization = new Mock<ICurrentUserAuthorization>();
        authorization.Setup(service => service.CanAccessTransferAsync(
                It.IsAny<ClaimsPrincipal>(), 3, 4, CompanyCapability.Post))
            .ReturnsAsync(true);
        var expected = new TransferDispatchView(
            1, 7, 12, Guid.NewGuid(), 41, 5, 3, 4, 90, 10, null, null, 12m, 120m,
            "dispatch-1", "warehouse-user", DateTimeOffset.UtcNow);
        transfers.Setup(service => service.DispatchAsync(
                7,
                12,
                10,
                "dispatch-1",
                "warehouse-user",
                It.IsAny<StockMutationScope>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var idempotency = new Mock<IIdempotencyKeyStore>();
        idempotency.Setup(store => store.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<Func<Task>>(),
                It.IsAny<CancellationToken>()))
            .Returns((string scope, string key, string hash, Func<Task> operation, CancellationToken cancellationToken) => operation());
        var controller = CreateController(transfers.Object, authorization.Object, idempotency.Object);
        controller.Request.Headers["Idempotency-Key"] = "dispatch-1";
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "warehouse-user")], "test"));

        var result = await controller.Dispatch(7, 12, new DispatchTransferOrderRequest(10), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        transfers.Verify(service => service.DispatchAsync(
            7, 12, 10, "dispatch-1", "warehouse-user", It.IsAny<StockMutationScope>(), It.IsAny<CancellationToken>()),
            Times.Once);
        idempotency.Verify(store => store.ExecuteAsync(
            It.IsAny<string>(), "dispatch-1", It.IsAny<string>(), It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Dispatch_checks_post_access_before_rejecting_invalid_quantity()
    {
        var order = new TransferOrderView(
            7, Guid.NewGuid(), "TO-2026-00001", 41, 3, 4, DateTime.UtcNow,
            TransferOrderStatus.Approved, null, []);
        var transfers = new Mock<ITransferOrderService>();
        transfers.Setup(service => service.GetByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(order);
        var authorization = new Mock<ICurrentUserAuthorization>();
        authorization.Setup(service => service.CanAccessTransferAsync(
                It.IsAny<ClaimsPrincipal>(), 3, 4, CompanyCapability.Post))
            .ReturnsAsync(false);
        var controller = CreateController(
            transfers.Object, authorization.Object, Mock.Of<IIdempotencyKeyStore>());
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "warehouse-user")], "test"));

        var result = await controller.Dispatch(7, 12, new DispatchTransferOrderRequest(0), CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        authorization.Verify(service => service.CanAccessTransferAsync(
            It.IsAny<ClaimsPrincipal>(), 3, 4, CompanyCapability.Post), Times.Once);
        transfers.Verify(service => service.DispatchAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<StockMutationScope>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Dispatch_returns_bad_request_for_invalid_quantity_after_authorization()
    {
        var order = new TransferOrderView(
            7, Guid.NewGuid(), "TO-2026-00001", 41, 3, 4, DateTime.UtcNow,
            TransferOrderStatus.Approved, null, []);
        var transfers = new Mock<ITransferOrderService>();
        transfers.Setup(service => service.GetByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(order);
        var authorization = new Mock<ICurrentUserAuthorization>();
        authorization.Setup(service => service.CanAccessTransferAsync(
                It.IsAny<ClaimsPrincipal>(), 3, 4, CompanyCapability.Post))
            .ReturnsAsync(true);
        var idempotency = new Mock<IIdempotencyKeyStore>();
        var controller = CreateController(transfers.Object, authorization.Object, idempotency.Object);
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "warehouse-user")], "test"));

        var result = await controller.Dispatch(7, 12, new DispatchTransferOrderRequest(0), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        authorization.Verify(service => service.CanAccessTransferAsync(
            It.IsAny<ClaimsPrincipal>(), 3, 4, CompanyCapability.Post), Times.Once);
        idempotency.Verify(store => store.ExecuteAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Func<Task>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        transfers.Verify(service => service.DispatchAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<StockMutationScope>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Dispatch_checks_actor_identity_before_rejecting_invalid_quantity()
    {
        var transfers = new Mock<ITransferOrderService>();
        var authorization = new Mock<ICurrentUserAuthorization>();
        var controller = CreateController(
            transfers.Object, authorization.Object, Mock.Of<IIdempotencyKeyStore>());

        var result = await controller.Dispatch(7, 12, new DispatchTransferOrderRequest(0), CancellationToken.None);

        result.Should().BeOfType<UnauthorizedObjectResult>();
        transfers.Verify(service => service.GetByIdAsync(7, It.IsAny<CancellationToken>()), Times.Never);
        authorization.Verify(service => service.CanAccessTransferAsync(
            It.IsAny<ClaimsPrincipal>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CompanyCapability>()), Times.Never);
    }

    [Fact]
    public async Task Dispatch_replay_reads_the_persisted_result_when_the_claim_skips_execution()
    {
        var order = new TransferOrderView(
            7, Guid.NewGuid(), "TO-2026-00001", 41, 3, 4, DateTime.UtcNow,
            TransferOrderStatus.Approved, null, []);
        var expected = new TransferDispatchView(
            2, 7, 12, Guid.NewGuid(), 41, 5, 3, 4, 91, 10, null, null, 12m, 120m,
            "dispatch-1", "warehouse-user", DateTimeOffset.UtcNow);
        var transfers = new Mock<ITransferOrderService>();
        transfers.Setup(service => service.GetByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(order);
        transfers.Setup(service => service.GetDispatchByKeyAsync(7, 12, "dispatch-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var authorization = new Mock<ICurrentUserAuthorization>();
        authorization.Setup(service => service.CanAccessTransferAsync(
                It.IsAny<ClaimsPrincipal>(), 3, 4, CompanyCapability.Post))
            .ReturnsAsync(true);
        var idempotency = new Mock<IIdempotencyKeyStore>();
        idempotency.Setup(store => store.ExecuteAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var controller = CreateController(transfers.Object, authorization.Object, idempotency.Object);
        controller.Request.Headers["Idempotency-Key"] = "dispatch-1";
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "warehouse-user")], "test"));

        var result = await controller.Dispatch(7, 12, new DispatchTransferOrderRequest(10), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        transfers.Verify(service => service.GetDispatchByKeyAsync(7, 12, "dispatch-1", It.IsAny<CancellationToken>()),
            Times.Once);
        transfers.Verify(service => service.DispatchAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<StockMutationScope>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetById_requires_view_access_to_the_stored_company()
    {
        var order = new TransferOrderView(
            7,
            Guid.NewGuid(),
            "TO-2026-00001",
            41,
            3,
            4,
            DateTime.UtcNow,
            TransferOrderStatus.Cancelled,
            "cancelled",
            []);
        var transferOrders = new Mock<ITransferOrderService>();
        transferOrders.Setup(service => service.GetByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(order);
        var authorization = new Mock<ICurrentUserAuthorization>();
        authorization.Setup(service => service.CanAccessCompanyAsync(
                It.IsAny<ClaimsPrincipal>(), 41, CompanyCapability.View))
            .ReturnsAsync(false);
        authorization.Setup(service => service.IsTenantAdministratorAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync(false);

        var controller = new TransferOrdersController(
            transferOrders.Object,
            authorization.Object,
            Mock.Of<IIdempotencyKeyStore>(),
            new TestTenantContext("tenant-a"))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.GetById(7, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        authorization.Verify(service => service.CanAccessTransferAsync(
            It.IsAny<ClaimsPrincipal>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CompanyCapability>()),
            Times.Never);
    }

    private static TransferOrdersController CreateController(
        ITransferOrderService transfers,
        ICurrentUserAuthorization authorization,
        IIdempotencyKeyStore idempotency) => new(
        transfers,
        authorization,
        idempotency,
        new TestTenantContext("tenant-a"))
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
    };
}
