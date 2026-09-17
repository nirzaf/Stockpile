namespace Merconiq.Web.Security;

/// <summary>Named authorization capabilities used by server endpoints.</summary>
public static class CapabilityPolicies
{
    public const string TenantAdministrator = "Tenant.Administrator";
    public const string View = "Capability.View";
    public const string Edit = "Capability.Edit";
    public const string Approve = "Capability.Approve";
    public const string Post = "Capability.Post";
    public const string Reverse = "Capability.Reverse";
    public const string Administer = "Capability.Administer";
    public const string OverrideExpiredStock = "Capability.OverrideExpiredStock";
}
