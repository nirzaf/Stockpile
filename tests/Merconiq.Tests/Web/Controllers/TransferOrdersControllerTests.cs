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
}
