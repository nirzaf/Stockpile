using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Microsoft.AspNetCore.Identity;

namespace Merconiq.Infrastructure.Data;

/// <summary>Provisions the first administrator for one explicitly selected tenant.</summary>
public sealed class AdminBootstrapService(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    ITenantContext tenantContext)
{
    public const string AdministratorRole = "Admin";

    /// <summary>
    /// Creates or reuses the tenant's administrator and required role. The operation is safe to
    /// repeat and never searches for users outside the selected tenant.
    /// </summary>
    public async Task BootstrapAsync(
        string tenantId,
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTenantId(tenantId);
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new ArgumentException("An administrator email is required.", nameof(email));
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("An administrator password is required.", nameof(password));
        }

        tenantContext.SetTenant(tenantId);
        await EnsureAdministratorRoleAsync();
        cancellationToken.ThrowIfCancellationRequested();

        var user = await userManager.FindByEmailAsync(email);
        if (user is null)
        {
            var candidate = new ApplicationUser
            {
                UserName = email,
                Email = email,
                FirstName = "Admin",
                LastName = "User",
                EmailConfirmed = true,
                TenantId = tenantId
            };

            var createResult = await userManager.CreateAsync(candidate, password);
            if (createResult.Succeeded)
            {
                user = candidate;
            }
            else
            {
                // A concurrent invocation may have created the same user. Re-read the
                // tenant-scoped user before treating the result as a real failure.
                user = await userManager.FindByEmailAsync(email);
                if (user is null)
                {
                    ThrowIdentityFailure("Creating the administrator", createResult);
                }
            }
        }

        if (!string.Equals(user.TenantId, tenantId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The administrator does not belong to the selected tenant.");
        }

        if (!await userManager.IsInRoleAsync(user, AdministratorRole))
        {
            var roleResult = await userManager.AddToRoleAsync(user, AdministratorRole);
            if (!roleResult.Succeeded && !await userManager.IsInRoleAsync(user, AdministratorRole))
            {
                ThrowIdentityFailure("Assigning the administrator role", roleResult);
            }
        }
    }

    private async Task EnsureAdministratorRoleAsync()
    {
        if (await roleManager.RoleExistsAsync(AdministratorRole))
        {
            return;
        }

        var result = await roleManager.CreateAsync(new IdentityRole(AdministratorRole));
        if (!result.Succeeded && !await roleManager.RoleExistsAsync(AdministratorRole))
        {
            ThrowIdentityFailure("Creating the administrator role", result);
        }
    }

    private static void ValidateTenantId(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId)
            || tenantId.Length > 64
            || tenantId.Any(character => !char.IsLetterOrDigit(character)
                && !(character is '-' or '_' or '.')))
        {
            throw new ArgumentException(
                "The tenant identifier must be 1-64 characters and contain only letters, digits, '-', '_', or '.'.",
                nameof(tenantId));
        }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowIdentityFailure(string operation, IdentityResult result)
    {
        var errors = string.Join(", ", result.Errors.Select(error => $"{error.Code}: {error.Description}"));
        throw new InvalidOperationException($"{operation} failed: {errors}");
    }
}
