namespace InventoryManagementSystem.Web.Security;

/// <summary>Configuration for durable ASP.NET Core Data Protection key storage.</summary>
public sealed class DataProtectionOptions
{
    public const string SectionName = "DataProtection";

    /// <summary>Shared mounted directory used by production instances and deployments.</summary>
    public string KeyStoragePath { get; set; } = "/app/data/keys";
}
