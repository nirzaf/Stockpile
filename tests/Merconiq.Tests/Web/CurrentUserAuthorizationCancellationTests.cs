using System.Security.Claims;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace Merconiq.Tests.Web;

public sealed class CurrentUserAuthorizationCancellationTests
{
    [Fact]
    public async Task CanAccessLocationAsync_observes_cancellation_before_creating_a_scope()
    {
        var scopeFactory = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        var authorization = new CurrentUserAuthorization(
            Mock.Of<ITenantContext>(),
            scopeFactory.Object,
            Options.Create(new IdentityOptions()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Func<Task> check = () => authorization.CanAccessLocationAsync(
            new ClaimsPrincipal(new ClaimsIdentity()),
            locationId: 7,
            capability: CompanyCapability.Post,
            cancellationToken: cancellation.Token);

        await check.Should().ThrowAsync<OperationCanceledException>();
        scopeFactory.VerifyNoOtherCalls();
    }
}
