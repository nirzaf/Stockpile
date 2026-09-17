using Merconiq.Core.Entities;

namespace Merconiq.Web.Components.Pages.PurchaseOrders;

internal static class PurchaseOrderAmendmentPresentation
{
    internal const string UnitPriceFormat = "N4";

    internal static IReadOnlyList<TaxRuleOption> GetTaxRuleOptions(
        IEnumerable<TaxRule> rules,
        int? selectedTaxRuleId,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var availableRules = rules.GroupBy(rule => rule.Id).Select(group => group.First()).ToList();
        var options = availableRules
            .Where(rule => IsReusable(rule, nowUtc))
            .OrderBy(rule => rule.Code, StringComparer.OrdinalIgnoreCase)
            .ThenBy(rule => rule.Id)
            .Select(rule => new TaxRuleOption(rule.Id, FormatRuleLabel(rule), IsHistorical: false))
            .ToList();

        if (!selectedTaxRuleId.HasValue)
        {
            return options;
        }

        var selectedRule = availableRules.FirstOrDefault(rule => rule.Id == selectedTaxRuleId.Value);
        if (selectedRule is null || IsReusable(selectedRule, nowUtc))
        {
            return options;
        }

        var reason = !selectedRule.IsActive
            ? "inactive"
            : selectedRule.EffectiveToUtc.HasValue && selectedRule.EffectiveToUtc.Value <= nowUtc
                ? "expired"
                : selectedRule.EffectiveFromUtc > nowUtc
                    ? "not yet effective"
                    : "not currently reusable";
        options.Add(new TaxRuleOption(
            selectedRule.Id,
            $"{FormatRuleLabel(selectedRule)} — Historical: {reason}; not available for new selections",
            IsHistorical: true));

        return options;
    }

    private static bool IsReusable(TaxRule rule, DateTime nowUtc) =>
        rule.IsActive &&
        rule.EffectiveFromUtc <= nowUtc &&
        (!rule.EffectiveToUtc.HasValue || rule.EffectiveToUtc.Value > nowUtc);

    private static string FormatRuleLabel(TaxRule rule) => $"{rule.Code} ({rule.RatePercent}%)";

    internal sealed record TaxRuleOption(int Id, string Label, bool IsHistorical);
}
