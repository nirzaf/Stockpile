using System.Security.Claims;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Merconiq.Web.Tenancy;

namespace Merconiq.Web.Controllers;

public class AccountController : Controller
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<AccountController> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITenantContext _tenantContext;
    private readonly IOptions<IdentityOptions> _identityOptions;

    public AccountController(
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        ILogger<AccountController> logger,
        IServiceScopeFactory scopeFactory,
        ITenantContext tenantContext,
        IOptions<IdentityOptions> identityOptions)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _tenantContext = tenantContext;
        _identityOptions = identityOptions;
    }

    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl;
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("Login")]
    public async Task<IActionResult> Login(string email, string password, string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl;

        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        {
            ModelState.AddModelError(string.Empty, "Email and password are required.");
            return View();
        }

        var user = await _userManager.FindByEmailAsync(email);
        if (user == null)
        {
            ModelState.AddModelError(string.Empty, "Invalid login attempt.");
            return View();
        }

        var result = await _signInManager.PasswordSignInAsync(user, password, isPersistent: false, lockoutOnFailure: true);
        if (result.Succeeded)
            return RedirectToLocal(returnUrl);

        if (result.IsLockedOut)
            ModelState.AddModelError(string.Empty, "Account locked. Try again later.");
        else
            ModelState.AddModelError(string.Empty, "Invalid login attempt.");

        return View();
    }

    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        var user = await _userManager.GetUserAsync(User);
        var stampResult = user is null
            ? IdentityResult.Success
            : await InvalidateExistingSessionsAsync(user);
        await _signInManager.SignOutAsync();

        if (!stampResult.Succeeded)
        {
            _logger.LogError("Could not invalidate existing sessions for user {UserId} during logout: {Errors}",
                user?.Id, string.Join(" ", stampResult.Errors.Select(error => error.Code)));
            return StatusCode(StatusCodes.Status500InternalServerError,
                "Signed out here, but other sessions could not be invalidated. Please retry or contact an administrator.");
        }

        return RedirectToAction("Login", "Account");
    }

    private async Task<IdentityResult> InvalidateExistingSessionsAsync(ApplicationUser user)
    {
        var result = await _userManager.UpdateSecurityStampAsync(user);
        if (result.Succeeded || !result.Errors.Any(error => error.Code == "ConcurrencyFailure"))
        {
            return result;
        }

        if (!_tenantContext.IsResolved)
        {
            return IdentityResult.Failed(new IdentityError
            {
                Code = "TenantUnavailable",
                Description = "The request tenant could not be resolved while invalidating sessions."
            });
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        if (!tenantContext.IsResolved)
        {
            tenantContext.SetTenant(_tenantContext.TenantId);
        }
        else if (!string.Equals(tenantContext.TenantId, _tenantContext.TenantId, StringComparison.Ordinal))
        {
            return IdentityResult.Failed(new IdentityError
            {
                Code = "TenantMismatch",
                Description = "The session tenant changed while invalidating sessions."
            });
        }

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var currentUser = await userManager.FindByIdAsync(user.Id);
        if (currentUser is null)
        {
            return IdentityResult.Success;
        }

        var currentSecurityStamp = await userManager.GetSecurityStampAsync(currentUser);
        var ticketSecurityStamp = User.FindFirstValue(
            _identityOptions.Value.ClaimsIdentity.SecurityStampClaimType);
        if (!string.IsNullOrWhiteSpace(ticketSecurityStamp) &&
            !string.Equals(ticketSecurityStamp, currentSecurityStamp, StringComparison.Ordinal))
        {
            // Another overlapping request has already changed the stamp, so the old
            // ticket and its circuits are already revoked.
            return IdentityResult.Success;
        }

        // The concurrency conflict was unrelated to session revocation; retry once
        // with the freshly loaded entity rather than failing an otherwise valid logout.
        return await userManager.UpdateSecurityStampAsync(currentUser);
    }

    [Authorize]
    public IActionResult AccessDenied() => View();

    private IActionResult RedirectToLocal(string? returnUrl)
    {
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);
        return RedirectToAction("Index", "Home");
    }
}
