using InventoryManagementSystem.Core.Diagnostics;
using InventoryManagementSystem.Core.Entities;
using InventoryManagementSystem.Core.Features.Items.Queries;
using InventoryManagementSystem.Core.Features.Stock.Queries;
using InventoryManagementSystem.Core.Models;
using InventoryManagementSystem.Core.Interfaces;
using InventoryManagementSystem.Infrastructure.Data;
using InventoryManagementSystem.Web.Services;
using InventoryManagementSystem.Web.Security;
using InventoryManagementSystem.Web.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;
using MediatR;

namespace InventoryManagementSystem.Web.Configuration;

public static class EndpointExtensions
{
    private static readonly string[] SupportedCultures = ["en-US", "ar-SA"];

    public static WebApplication MapInventoryEndpoints(this WebApplication app)
    {
        app.MapGet("/culture/set", (HttpContext httpContext, string culture, string? returnUrl) =>
        {
            var selectedCulture = SupportedCultures.FirstOrDefault(
                supported => string.Equals(supported, culture, StringComparison.OrdinalIgnoreCase));
            if (selectedCulture == null)
            {
                return Results.BadRequest("Unsupported culture.");
            }

            httpContext.Response.Cookies.Append(
                CookieRequestCultureProvider.DefaultCookieName,
                CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(selectedCulture)));

            var destination = !string.IsNullOrWhiteSpace(returnUrl) && returnUrl.StartsWith('/')
                ? returnUrl
                : "/";
            return Results.LocalRedirect(destination);
        });

        app.MapControllerRoute(
            name: "default",
            pattern: "{controller=Home}/{action=Index}/{id?}");
        app.MapRazorPages();
        app.MapRazorComponents<Components.App>()
            .AddInteractiveServerRenderMode();
        app.MapHealthChecks("/health");

        var v1 = app.MapGroup("/api/v1")
            .WithTags("API v1")
            .RequireAuthorization("Api")
            .RequireRateLimiting("Api");

        v1.MapPost("/auth/token", async (
            TokenRequest request,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            ITenantContext tenantContext,
            JwtTokenOptions jwtOptions) =>
        {
            var user = await userManager.FindByNameAsync(request.Username)
                ?? await userManager.FindByEmailAsync(request.Username);
            if (user == null)
            {
                return Results.Unauthorized();
            }

            if (!tenantContext.IsResolved ||
                !string.Equals(user.TenantId, tenantContext.TenantId, StringComparison.Ordinal))
            {
                return Results.Unauthorized();
            }

            var result = await signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
            if (!result.Succeeded)
            {
                return Results.Unauthorized();
            }

            var tokenHandler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
            var roles = await userManager.GetRolesAsync(user);
            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new System.Security.Claims.ClaimsIdentity(
                    JwtClaimsFactory.Create(user, tenantContext.TenantId, roles)),
                Expires = DateTime.UtcNow.AddHours(2),
                Issuer = jwtOptions.Issuer,
                Audience = jwtOptions.Audience,
                SigningCredentials = new SigningCredentials(
                    new SymmetricSecurityKey(jwtOptions.SigningKey),
                    SecurityAlgorithms.HmacSha256Signature)
            };
            var token = tokenHandler.CreateToken(tokenDescriptor);
            return Results.Ok(new
            {
                Token = tokenHandler.WriteToken(token),
                Expires = tokenDescriptor.Expires
            });
        })
        .AllowAnonymous()
        .RequireRateLimiting("Login")
        .WithName("GenerateToken")
        .WithTags("Auth");

        v1.MapGet("/webhooks", async (IRepository<WebhookSubscription> repository) =>
        {
            var subscriptions = (await repository.GetAllAsync())
                .Select(ToWebhookResponse)
                .ToList();
            return Results.Ok(ApiResponse<IReadOnlyList<WebhookSubscriptionResponse>>.CreateSuccess(subscriptions));
        })
            .WithName("GetWebhooks")
            .WithTags("Webhooks")
            .RequireAuthorization(policy => policy.RequireRole("Admin", "Manager"));

        v1.MapPost("/webhooks", async (
            WebhookSubscriptionRequest request,
            IRepository<WebhookSubscription> repository,
            IUnitOfWork unitOfWork) =>
        {
            var validationError = await ValidateWebhookRequestAsync(request);
            if (validationError != null)
            {
                return Results.BadRequest(ApiResponse<object>.CreateFailure(validationError));
            }

            var subscription = await repository.AddAsync(new WebhookSubscription
            {
                Url = request.Url.Trim(),
                EventType = request.EventType.Trim(),
                Secret = request.Secret,
                IsActive = request.IsActive
            });
            await unitOfWork.SaveChangesAsync();
            return Results.Ok(ApiResponse<WebhookSubscriptionResponse>.CreateSuccess(ToWebhookResponse(subscription)));
        })
            .WithName("CreateWebhook")
            .WithTags("Webhooks")
            .RequireAuthorization(policy => policy.RequireRole("Admin", "Manager"));

        v1.MapPut("/webhooks/{id:int}", async (
            int id,
            WebhookSubscriptionRequest request,
            IRepository<WebhookSubscription> repository,
            IUnitOfWork unitOfWork) =>
        {
            var validationError = await ValidateWebhookRequestAsync(request);
            if (validationError != null)
            {
                return Results.BadRequest(ApiResponse<object>.CreateFailure(validationError));
            }

            var subscription = await repository.GetByIdAsync(id);
            if (subscription == null)
            {
                return Results.NotFound(ApiResponse<object>.CreateFailure("Webhook subscription not found."));
            }

            subscription.Url = request.Url.Trim();
            subscription.EventType = request.EventType.Trim();
            subscription.Secret = request.Secret;
            subscription.IsActive = request.IsActive;
            await repository.UpdateAsync(subscription);
            await unitOfWork.SaveChangesAsync();
            return Results.Ok(ApiResponse<WebhookSubscriptionResponse>.CreateSuccess(ToWebhookResponse(subscription)));
        })
            .WithName("UpdateWebhook")
            .WithTags("Webhooks")
            .RequireAuthorization(policy => policy.RequireRole("Admin", "Manager"));

        v1.MapDelete("/webhooks/{id:int}", async (
            int id,
            IRepository<WebhookSubscription> repository,
            IUnitOfWork unitOfWork) =>
        {
            var subscription = await repository.GetByIdAsync(id);
            if (subscription == null)
            {
                return Results.NotFound(ApiResponse<object>.CreateFailure("Webhook subscription not found."));
            }

            await repository.DeleteAsync(subscription);
            await unitOfWork.SaveChangesAsync();
            return Results.NoContent();
        })
            .WithName("DeleteWebhook")
            .WithTags("Webhooks")
            .RequireAuthorization(policy => policy.RequireRole("Admin", "Manager"));

        v1.MapGet("/forecast/{itemId:int}", async (
            int itemId,
            int? horizon,
            IMediator mediator,
            IMemoryCache cache,
            ITenantContext tenantContext) =>
        {
            var horizonDays = horizon ?? 30;
            var cacheKey = TenantCacheKeys.ForecastForItem(tenantContext.TenantId, itemId, horizonDays);
            var forecast = await cache.GetOrCreateAsync(cacheKey, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                return await mediator.Send(new ForecastDemandQuery(itemId, horizonDays));
            });
            return Results.Ok(ApiResponse<DemandForecastResult>.CreateSuccess(forecast!));
        })
            .WithName("ForecastDemand")
            .WithTags("AI")
            .RequireRateLimiting("Ai");

        v1.MapGet("/forecast", async (
            int? horizon,
            IMediator mediator,
            IMemoryCache cache,
            ITenantContext tenantContext) =>
        {
            var horizonDays = horizon ?? 30;
            var cacheKey = TenantCacheKeys.ForecastForAllItems(tenantContext.TenantId, horizonDays);
            var forecasts = await cache.GetOrCreateAsync(cacheKey, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                return await mediator.Send(new ForecastAllItemsDemandQuery(horizonDays));
            });
            return Results.Ok(ApiResponse<IReadOnlyList<DemandForecastResult>>.CreateSuccess(forecasts!));
        })
            .WithName("ForecastAllDemand")
            .WithTags("AI")
            .RequireRateLimiting("Ai");

        v1.MapGet("/anomalies", async (DateTime? from, DateTime? to, IMediator mediator) =>
        {
            var anomalies = await mediator.Send(new DetectAnomaliesQuery(from, to));
            return Results.Ok(ApiResponse<IReadOnlyList<StockAnomaly>>.CreateSuccess(anomalies));
        })
            .WithName("DetectAnomalies")
            .WithTags("AI")
            .RequireRateLimiting("Ai");

        return app;
    }

    private static async Task<string?> ValidateWebhookRequestAsync(WebhookSubscriptionRequest request)
    {
        var urlError = await WebhookUrlValidator.ValidateAsync(request.Url);
        if (urlError != null)
        {
            return urlError;
        }

        if (string.IsNullOrWhiteSpace(request.EventType) || request.EventType.Length > 100)
        {
            return "EventType is required and must be 100 characters or fewer.";
        }

        if (request.Secret?.Length > 512)
        {
            return "Secret must be 512 characters or fewer.";
        }

        return null;
    }

    private static WebhookSubscriptionResponse ToWebhookResponse(WebhookSubscription subscription) =>
        new(subscription.Id, subscription.Url, subscription.EventType, subscription.IsActive);
}
