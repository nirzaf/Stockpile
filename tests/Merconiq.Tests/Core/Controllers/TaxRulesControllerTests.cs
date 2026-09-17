using System.Linq.Expressions;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Services;
using Merconiq.Tests.Infrastructure;
using Merconiq.Web.Controllers.Api.V1;
using Moq;

namespace Merconiq.Tests.Core.Controllers;

public sealed class TaxRulesControllerTests
{
    [Fact]
    public async Task Create_StoresAnEffectiveDatedRule()
    {
        var repository = new Mock<IRepository<TaxRule>>();
        repository
            .Setup(value => value.FindAsync(It.IsAny<Expression<Func<TaxRule, bool>>>()))
            .ReturnsAsync([]);
        var unitOfWork = new Mock<IUnitOfWork>();
        SetupTransaction(unitOfWork);
        var controller = new TaxRulesController(repository.Object, unitOfWork.Object);
        var effectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = await controller.Create(new TaxRuleRequest(
            "STANDARD-15",
            TaxCategory.Standard,
            15m,
            TaxCalculationMode.Exclusive,
            effectiveFrom,
            null));

        result.Should().BeOfType<Microsoft.AspNetCore.Mvc.CreatedAtActionResult>();
        repository.Verify(value => value.AddAsync(It.Is<TaxRule>(rule =>
            rule.Code == "STANDARD-15" &&
            rule.RatePercent == 15m &&
            rule.EffectiveFromUtc == effectiveFrom)), Times.Once);
        unitOfWork.Verify(value => value.SaveChangesAsync(default), Times.Once);
    }

    [Fact]
    public async Task Create_RejectsOverlappingPeriodsForTheSameCode()
    {
        var repository = new Mock<IRepository<TaxRule>>();
        repository
            .Setup(value => value.FindAsync(It.IsAny<Expression<Func<TaxRule, bool>>>()))
            .ReturnsAsync([
                new TaxRule
                {
                    Code = "STANDARD-15",
                    Category = TaxCategory.Standard,
                    RatePercent = 15m,
                    EffectiveFromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    EffectiveToUtc = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
                }
            ]);
        var unitOfWork = new Mock<IUnitOfWork>();
        SetupTransaction(unitOfWork);
        var controller = new TaxRulesController(repository.Object, unitOfWork.Object);

        var result = await controller.Create(new TaxRuleRequest(
            "STANDARD-15",
            TaxCategory.Standard,
            15m,
            TaxCalculationMode.Exclusive,
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            null));

        result.Should().BeOfType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>();
        repository.Verify(value => value.AddAsync(It.IsAny<TaxRule>()), Times.Never);
        unitOfWork.Verify(value => value.SaveChangesAsync(default), Times.Never);
    }

    [Fact]
    public async Task Create_RejectsNonZeroExemptRate()
    {
        var controller = new TaxRulesController(
            new Mock<IRepository<TaxRule>>().Object,
            new Mock<IUnitOfWork>().Object);

        var result = await controller.Create(new TaxRuleRequest(
            "EXEMPT",
            TaxCategory.Exempt,
            5m,
            TaxCalculationMode.Exclusive,
            DateTime.UtcNow,
            null));

        result.Should().BeOfType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>();
    }

    [Fact]
    public void TaxRuleEndpointsRequireTenantAdministratorAuthority()
    {
        foreach (var actionName in new[] { nameof(TaxRulesController.GetAll), nameof(TaxRulesController.GetById), nameof(TaxRulesController.Create) })
        {
            var authorization = typeof(TaxRulesController).GetMethod(actionName)!
                .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true)
                .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
                .Single();
            authorization.Policy.Should().Be(Merconiq.Web.Security.CapabilityPolicies.TenantAdministrator);
        }
    }

    private static void SetupTransaction(Mock<IUnitOfWork> unitOfWork)
    {
        unitOfWork
            .Setup(value => value.ExecuteInTransactionAsync(
                It.IsAny<Func<Task>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Func<Task<bool>>?>()))
            .Returns((Func<Task> operation, CancellationToken _, Func<Task<bool>>? _) => operation());
        unitOfWork
            .Setup(value => value.AcquireTenantOperationLockAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        unitOfWork.Setup(value => value.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
    }
}
