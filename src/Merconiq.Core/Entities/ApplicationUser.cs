using Microsoft.AspNetCore.Identity;
using Merconiq.Core.Interfaces;

namespace Merconiq.Core.Entities;

/// <summary>Application identity user shared by the domain and persistence layers.</summary>
public class ApplicationUser : IdentityUser, ITenantScoped
{
    public string TenantId { get; set; } = "default";
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? DisplayName => $"{FirstName} {LastName}".Trim();
}
