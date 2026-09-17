using System.Globalization;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Web.Components.Pages.PurchaseOrders;

namespace Merconiq.Tests.Web.Components;

public sealed class PurchaseOrderAmendmentPresentationTests
{
    [Fact]
    public void UnitPriceFormat_PreservesFourFractionalDigitsAtTwoCurrencyDecimals()
    {
        PurchaseOrderAmendmentPresentation.UnitPriceFormat.Should().Be("N4");
        1.2345m.ToString(PurchaseOrderAmendmentPresentation.UnitPriceFormat, CultureInfo.InvariantCulture)
            .Should().Be("1.2345");
    }

    [Fact]
    public void TaxRuleOptions_ExposeOnlyTheSelectedInactiveRuleAsDisabledHistoricalOption()
    {
        var nowUtc = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);
        var activeRule = CreateRule(1, "STANDARD", nowUtc.AddDays(-10));
        var selectedInactiveRule = CreateRule(2, "OLD-STANDARD", nowUtc.AddDays(-100), isActive: false);
        var unrelatedInactiveRule = CreateRule(3, "UNRELATED", nowUtc.AddDays(-100), isActive: false);
        var expiredRule = CreateRule(4, "EXPIRED", nowUtc.AddDays(-100), effectiveToUtc: nowUtc);

        var options = PurchaseOrderAmendmentPresentation.GetTaxRuleOptions(
            [activeRule, selectedInactiveRule, unrelatedInactiveRule, expiredRule],
            selectedInactiveRule.Id,
            nowUtc);

        options.Should().HaveCount(2);
        options.Should().ContainSingle(option => option.Id == activeRule.Id && !option.IsHistorical);
        var historicalOption = options.Should().ContainSingle(option => option.Id == selectedInactiveRule.Id).Subject;
        historicalOption.IsHistorical.Should().BeTrue();
        historicalOption.Label.Should().Contain("Historical: inactive");
        historicalOption.Label.Should().Contain("not available for new selections");
        options.Should().NotContain(option => option.Id == unrelatedInactiveRule.Id || option.Id == expiredRule.Id);
    }

    [Fact]
    public void TaxRuleOptions_LabelASelectedExpiredRuleAsHistoricalAndExcludeItFromOtherLines()
    {
        var nowUtc = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);
        var expiredRule = CreateRule(5, "OLD-RATE", nowUtc.AddDays(-100), effectiveToUtc: nowUtc);

        var selectedOptions = PurchaseOrderAmendmentPresentation.GetTaxRuleOptions(
            [expiredRule], expiredRule.Id, nowUtc);
        var otherLineOptions = PurchaseOrderAmendmentPresentation.GetTaxRuleOptions(
            [expiredRule], null, nowUtc);

        selectedOptions.Should().ContainSingle().Which.Label.Should().Contain("Historical: expired");
        selectedOptions.Single().IsHistorical.Should().BeTrue();
        otherLineOptions.Should().BeEmpty();
    }

    private static TaxRule CreateRule(
        int id,
        string code,
        DateTime effectiveFromUtc,
        bool isActive = true,
        DateTime? effectiveToUtc = null) => new()
    {
        Id = id,
        Code = code,
        RatePercent = 7.5m,
        EffectiveFromUtc = effectiveFromUtc,
        EffectiveToUtc = effectiveToUtc,
        IsActive = isActive
    };
}
