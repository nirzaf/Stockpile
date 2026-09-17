using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Infrastructure.Services;

/// <summary>Persists tenant-checked company grants and revokes existing Identity sessions.</summary>
public sealed class CompanyMembershipService(
    InventoryDbContext db,
    UserManager<ApplicationUser> userManager,
    IUnitOfWork unitOfWork) : ICompanyMembershipService
{
    private const CompanyCapability AllCapabilities =
        CompanyCapability.View | CompanyCapability.Edit | CompanyCapability.Approve |
        CompanyCapability.Post | CompanyCapability.Reverse | CompanyCapability.Administer |
        CompanyCapability.OverrideExpiredStock | CompanyCapability.OverrideQuarantinedStock;

    public async Task<IReadOnlyList<CompanyMembership>> GetCompanyMembershipsAsync(int companyId)
    {
        await EnsureCompanyExistsAsync(companyId);
        return await db.CompanyMemberships
            .Where(membership => membership.CompanyId == companyId)
            .OrderBy(membership => membership.UserId)
            .ToListAsync();
    }

    public async Task SetCapabilitiesAsync(int companyId, string userId, CompanyCapability capabilities)
    {
        ValidateCapabilities(capabilities);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        await unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            await EnsureCompanyExistsAsync(companyId);
            var user = await userManager.FindByIdAsync(userId)
                ?? throw new KeyNotFoundException("User not found in the current tenant.");
            var membership = await db.CompanyMemberships.SingleOrDefaultAsync(existing =>
                existing.CompanyId == companyId && existing.UserId == userId);
            var effectiveCapabilities = capabilities == CompanyCapability.None
                ? CompanyCapability.View
                : capabilities | CompanyCapability.View;

            if (membership is null)
            {
                db.CompanyMemberships.Add(new CompanyMembership
                {
                    CompanyId = companyId,
                    UserId = userId,
                    Capabilities = effectiveCapabilities,
                    IsActive = capabilities != CompanyCapability.None
                });
            }
            else
            {
                membership.Capabilities = effectiveCapabilities;
                membership.IsActive = capabilities != CompanyCapability.None;
            }

            await unitOfWork.SaveChangesAsync();
            var stampResult = await userManager.UpdateSecurityStampAsync(user);
            EnsureSucceeded(stampResult, "Could not invalidate the user's existing sessions.");
        });
    }

    public async Task RemoveMembershipAsync(int companyId, string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        await unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            await EnsureCompanyExistsAsync(companyId);
            var membership = await db.CompanyMemberships.SingleOrDefaultAsync(existing =>
                existing.CompanyId == companyId && existing.UserId == userId);
            if (membership is null)
            {
                return;
            }

            var user = await userManager.FindByIdAsync(userId)
                ?? throw new KeyNotFoundException("User not found in the current tenant.");
            db.CompanyMemberships.Remove(membership);
            await unitOfWork.SaveChangesAsync();
            var stampResult = await userManager.UpdateSecurityStampAsync(user);
            EnsureSucceeded(stampResult, "Could not invalidate the user's existing sessions.");
        });
    }

    private async Task EnsureCompanyExistsAsync(int companyId)
    {
        if (!await db.Companies.AnyAsync(company => company.Id == companyId))
        {
            throw new KeyNotFoundException("Company not found in the current tenant.");
        }
    }

    private static void ValidateCapabilities(CompanyCapability capabilities)
    {
        if ((capabilities & ~AllCapabilities) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capabilities), "Unknown company capability bits are not allowed.");
        }
    }

    private static void EnsureSucceeded(IdentityResult result, string message)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(message + " " +
                string.Join("; ", result.Errors.Select(error => error.Code)));
        }
    }
}
