using Microsoft.AspNetCore.Identity;

namespace InventoryManagementSystem.Core.Entities;

/// <summary>Application identity user shared by the domain and persistence layers.</summary>
public class ApplicationUser : IdentityUser
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? DisplayName => $"{FirstName} {LastName}".Trim();
}
