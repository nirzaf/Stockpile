using System.Security.Claims;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Web.Controllers.Api.V1;
using Merconiq.Web.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Merconiq.Tests.Web.Controllers;

public sealed class StockCountsControllerTests
{
    [Fact]
    public async Task Start_forbids_an_unapproved_location_without_creating_a_snapshot()
    {
        const int locationId = 42;
        var stockCounts = new Mock<IStockCountService>(MockBehavior.Strict);
        var authorization = new Mock<ICurrentUserAuthorization>();
        authorization
            .Setup(service => service.CanAccessLocationAsync(
                It.IsAny<ClaimsPrincipal>(), locationId, CompanyCapability.Post))
            .ReturnsAsync(false);

        var controller = new StockCountsController(stockCounts.Object, authorization.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity())
                }
            }
        };

        var result = await controller.Start(new StartStockCountRequest(locationId), CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        stockCounts.Verify(service => service.StartAsync(
            It.IsAny<int>(),
            It.IsAny<StockMutationScope>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
