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
    public async Task Reconciliation_forbids_a_user_without_view_access_to_the_location()
    {
        const int locationId = 42;
        var stockCounts = new Mock<IStockCountService>(MockBehavior.Strict);
        var authorization = new Mock<ICurrentUserAuthorization>();
        authorization
            .Setup(service => service.CanAccessLocationAsync(
                It.IsAny<ClaimsPrincipal>(), locationId, CompanyCapability.View, It.IsAny<CancellationToken>()))
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

        var result = await controller.Reconcile(
            new StockCountReconciliationRequest(locationId),
            CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        stockCounts.Verify(service => service.GetReconciliationAsync(
            It.IsAny<StockCountReconciliationRequest>(),
            It.IsAny<IReadOnlyCollection<int>?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Reconciliation_passes_request_cancellation_to_company_scope_lookup()
    {
        const int locationId = 42;
        using var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        var stockCounts = new Mock<IStockCountService>(MockBehavior.Strict);
        stockCounts
            .Setup(service => service.GetReconciliationAsync(
                It.IsAny<StockCountReconciliationRequest>(),
                It.IsAny<IReadOnlyCollection<int>?>(),
                cancellationToken))
            .ReturnsAsync((StockCountReconciliationView?)null);
        var authorization = new Mock<ICurrentUserAuthorization>(MockBehavior.Strict);
        authorization
            .Setup(service => service.CanAccessLocationAsync(
                It.IsAny<ClaimsPrincipal>(), locationId, CompanyCapability.View, cancellationToken))
            .ReturnsAsync(true);
        authorization
            .Setup(service => service.IsTenantAdministratorAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync(false);
        authorization
            .Setup(service => service.GetAccessibleCompanyIdsAsync(
                It.IsAny<ClaimsPrincipal>(), CompanyCapability.View, cancellationToken))
            .ReturnsAsync((IReadOnlySet<int>)new HashSet<int> { 9 });

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

        var result = await controller.Reconcile(
            new StockCountReconciliationRequest(locationId),
            cancellationToken);

        result.Should().BeOfType<NotFoundObjectResult>();
        authorization.Verify(service => service.GetAccessibleCompanyIdsAsync(
            It.IsAny<ClaimsPrincipal>(), CompanyCapability.View, cancellationToken), Times.Once);
    }

    [Fact]
    public async Task Post_variance_forbids_a_user_without_approve_capability()
    {
        const int countId = 7;
        const int lineId = 3;
        const int locationId = 42;
        var stockCounts = new Mock<IStockCountService>(MockBehavior.Strict);
        stockCounts
            .Setup(service => service.GetAuthorizationContextAsync(countId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StockCountAuthorizationContext(locationId, 9));
        var authorization = new Mock<ICurrentUserAuthorization>();
        authorization
            .Setup(service => service.CanAccessLocationAsync(
                It.IsAny<ClaimsPrincipal>(), locationId, CompanyCapability.Approve, It.IsAny<CancellationToken>()))
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

        var result = await controller.PostVariance(
            countId,
            lineId,
            new PostStockCountVarianceRequest("cycle count adjustment"),
            CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        stockCounts.Verify(service => service.PostVarianceAsync(
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<decimal?>(),
            It.IsAny<StockMutationScope>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Post_variance_forbids_an_approver_without_post_capability()
    {
        const int countId = 7;
        const int lineId = 3;
        const int locationId = 42;
        var stockCounts = new Mock<IStockCountService>(MockBehavior.Strict);
        stockCounts
            .Setup(service => service.GetAuthorizationContextAsync(countId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StockCountAuthorizationContext(locationId, 9));
        var authorization = new Mock<ICurrentUserAuthorization>(MockBehavior.Strict);
        authorization
            .Setup(service => service.CanAccessLocationAsync(
                It.IsAny<ClaimsPrincipal>(), locationId, CompanyCapability.Approve, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        authorization
            .Setup(service => service.CanAccessLocationAsync(
                It.IsAny<ClaimsPrincipal>(), locationId, CompanyCapability.Post, It.IsAny<CancellationToken>()))
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

        var result = await controller.PostVariance(
            countId,
            lineId,
            new PostStockCountVarianceRequest("cycle count adjustment"),
            CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        stockCounts.Verify(service => service.PostVarianceAsync(
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<decimal?>(),
            It.IsAny<StockMutationScope>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Post_variance_rechecks_both_approve_and_post_authority_for_stock_mutation()
    {
        const int countId = 7;
        const int lineId = 3;
        const int locationId = 42;
        using var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        StockMutationScope? capturedScope = null;
        var stockCounts = new Mock<IStockCountService>(MockBehavior.Strict);
        stockCounts
            .Setup(service => service.GetAuthorizationContextAsync(countId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StockCountAuthorizationContext(locationId, 9));
        stockCounts
            .Setup(service => service.PostVarianceAsync(
                countId,
                lineId,
                "cycle count adjustment",
                null,
                It.IsAny<StockMutationScope>(),
                cancellationToken))
            .Callback<int, int, string, decimal?, StockMutationScope, CancellationToken>(
                (_, _, _, _, scope, _) => capturedScope = scope)
            .ReturnsAsync((StockCountLineView?)null);
        var authorization = new Mock<ICurrentUserAuthorization>(MockBehavior.Strict);
        authorization
            .Setup(service => service.CanAccessLocationAsync(
                It.IsAny<ClaimsPrincipal>(), locationId, CompanyCapability.Approve, cancellationToken))
            .ReturnsAsync(true);
        authorization
            .Setup(service => service.CanAccessLocationAsync(
                It.IsAny<ClaimsPrincipal>(), locationId, CompanyCapability.Post, cancellationToken))
            .ReturnsAsync(true);

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

        await controller.PostVariance(
            countId,
            lineId,
            new PostStockCountVarianceRequest("cycle count adjustment"),
            cancellationToken);

        capturedScope.Should().NotBeNull();
        var postedScope = capturedScope.GetValueOrDefault();
        (await postedScope.Reauthorize!(cancellationToken)).Should().BeTrue();
        authorization.Verify(service => service.CanAccessLocationAsync(
            It.IsAny<ClaimsPrincipal>(), locationId, CompanyCapability.Approve, cancellationToken), Times.Exactly(2));
        authorization.Verify(service => service.CanAccessLocationAsync(
            It.IsAny<ClaimsPrincipal>(), locationId, CompanyCapability.Post, cancellationToken), Times.Exactly(2));
    }

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
