using Merconiq.Core.Diagnostics;
using Merconiq.Core.Entities;
using Merconiq.Core.Features.Items.Queries;
using Merconiq.Core.Features.Stock.Queries;
using Merconiq.Core.Models;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Options;
using Merconiq.Infrastructure.Data;
using Merconiq.Web.Services;
using Merconiq.Web.Security;
using Merconiq.Web.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MediatR;

namespace Merconiq.Web.Configuration;

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
            JwtTokenOptions jwtOptions,
            IOptions<IdentityOptions> identityOptions) =>
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
                    JwtClaimsFactory.Create(
                        user,
                        tenantContext.TenantId,
                        roles,
                        identityOptions.Value.ClaimsIdentity.SecurityStampClaimType)),
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
            .RequireAuthorization(CapabilityPolicies.TenantAdministrator);

        v1.MapGet("/webhooks/deliveries", async (
            int? page,
            int? pageSize,
            InventoryDbContext db,
            CancellationToken cancellationToken) =>
        {
            var requestedPage = page ?? 1;
            var requestedPageSize = pageSize ?? 50;
            if (requestedPage < 1 || requestedPageSize is < 1 or > 100)
            {
                return Results.BadRequest(ApiResponse<object>.CreateFailure(
                    "Page must be positive and pageSize must be between 1 and 100."));
            }

            var skip = (long)(requestedPage - 1) * requestedPageSize;
            if (skip > int.MaxValue)
            {
                return Results.BadRequest(ApiResponse<object>.CreateFailure("Page is too large."));
            }

            // Project only safe operational metadata; payloads, endpoints, lease tokens,
            // response bodies and arbitrary diagnostic text remain private. The only
            // exposed failure reason is an allowlisted fixed payload-limit message.
            var rows = await db.WebhookDeliveries
                .AsNoTracking()
                .OrderByDescending(delivery => delivery.CreatedAt)
                .ThenByDescending(delivery => delivery.Id)
                .Skip((int)skip)
                .Take(requestedPageSize + 1)
                .Select(delivery => new
                {
                    delivery.Id,
                    delivery.EventId,
                    delivery.SubscriptionId,
                    delivery.EventType,
                    delivery.Status,
                    delivery.AttemptCount,
                    delivery.NextAttemptAt,
                    delivery.LastAttemptAt,
                    delivery.LastStatusCode,
                    delivery.DeliveredAt,
                    delivery.CreatedAt,
                    IsOversizedPayload = delivery.LastError == WebhookPayloadPolicy.OversizedEnvelopeDiagnostic
                })
                .ToListAsync(cancellationToken);

            var hasMore = rows.Count > requestedPageSize;
            var deliveries = rows.Take(requestedPageSize)
                .Select(delivery => new WebhookDeliveryDiagnosticResponse(
                    delivery.Id,
                    delivery.EventId,
                    delivery.SubscriptionId,
                    delivery.EventType,
                    delivery.Status.ToString(),
                    delivery.AttemptCount,
                    delivery.NextAttemptAt,
                    delivery.LastAttemptAt,
                    delivery.LastStatusCode,
                    delivery.DeliveredAt,
                    delivery.CreatedAt,
                    delivery.IsOversizedPayload ? WebhookPayloadPolicy.OversizedEnvelopeDiagnostic : null))
                .ToArray();

            var result = new WebhookDeliveryDiagnosticsPageResponse(
                requestedPage,
                requestedPageSize,
                hasMore,
                deliveries);
            return Results.Ok(ApiResponse<WebhookDeliveryDiagnosticsPageResponse>.CreateSuccess(result));
        })
            .WithName("GetWebhookDeliveryDiagnostics")
            .WithTags("Webhooks")
            .RequireAuthorization(CapabilityPolicies.TenantAdministrator);

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
            .RequireAuthorization(CapabilityPolicies.TenantAdministrator);

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
            .RequireAuthorization(CapabilityPolicies.TenantAdministrator);

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
            .RequireAuthorization(CapabilityPolicies.TenantAdministrator);

        v1.MapGet("/forecast/{itemId:int}", async (
            int itemId,
            int? horizon,
            IMediator mediator,
            IMemoryCache cache,
            ITenantContext tenantContext,
            ICurrentUserAuthorization authorization,
            IOptions<ForecastingOptions> forecastingOptions,
            HttpContext httpContext) =>
        {
            var tenantAdministrator = await authorization.IsTenantAdministratorAsync(httpContext.User);
            IReadOnlyCollection<int>? companyIds = null;
            if (!tenantAdministrator)
            {
                companyIds = (await authorization.GetAccessibleCompanyIdsAsync(
                    httpContext.User, CompanyCapability.View)).ToArray();
                if (companyIds.Count == 0)
                {
                    return Results.Forbid();
                }
            }

            if (!forecastingOptions.Value.Enabled)
            {
                return ForecastingUnavailable();
            }

            var horizonDays = horizon ?? 30;
            var cacheKey = TenantCacheKeys.ForecastForItem(
                tenantContext.TenantId, itemId, horizonDays, companyIds);
            var forecast = await cache.GetOrCreateAsync(cacheKey, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                return await mediator.Send(new ForecastDemandQuery(itemId, horizonDays, companyIds));
            });
            return Results.Ok(ApiResponse<DemandForecastResult>.CreateSuccess(forecast!));
        })
            .WithName("ForecastDemand")
            .WithTags("AI")
            .RequireAuthorization(CapabilityPolicies.View)
            .RequireRateLimiting("Ai");

        v1.MapGet("/forecast", async (
            int? horizon,
            IMediator mediator,
            IMemoryCache cache,
            ITenantContext tenantContext,
            ICurrentUserAuthorization authorization,
            IOptions<ForecastingOptions> forecastingOptions,
            HttpContext httpContext) =>
        {
            var tenantAdministrator = await authorization.IsTenantAdministratorAsync(httpContext.User);
            IReadOnlyCollection<int>? companyIds = null;
            if (!tenantAdministrator)
            {
                companyIds = (await authorization.GetAccessibleCompanyIdsAsync(
                    httpContext.User, CompanyCapability.View)).ToArray();
                if (companyIds.Count == 0)
                {
                    return Results.Forbid();
                }
            }

            if (!forecastingOptions.Value.Enabled)
            {
                return ForecastingUnavailable();
            }

            var horizonDays = horizon ?? 30;
            var cacheKey = TenantCacheKeys.ForecastForAllItems(
                tenantContext.TenantId, horizonDays, companyIds);
            var forecasts = await cache.GetOrCreateAsync(cacheKey, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                return await mediator.Send(new ForecastAllItemsDemandQuery(horizonDays, companyIds));
            });
            return Results.Ok(ApiResponse<IReadOnlyList<DemandForecastResult>>.CreateSuccess(forecasts!));
        })
            .WithName("ForecastAllDemand")
            .WithTags("AI")
            .RequireAuthorization(CapabilityPolicies.View)
            .RequireRateLimiting("Ai");

        v1.MapGet("/anomalies", async (
            DateTime? from,
            DateTime? to,
            IMediator mediator,
            ICurrentUserAuthorization authorization,
            HttpContext httpContext) =>
        {
            var tenantAdministrator = await authorization.IsTenantAdministratorAsync(httpContext.User);
            IReadOnlyCollection<int>? companyIds = null;
            if (!tenantAdministrator)
            {
                companyIds = (await authorization.GetAccessibleCompanyIdsAsync(
                    httpContext.User, CompanyCapability.View)).ToArray();
                if (companyIds.Count == 0)
                {
                    return Results.Forbid();
                }
            }

            var anomalies = await mediator.Send(new DetectAnomaliesQuery(from, to, companyIds));
            return Results.Ok(ApiResponse<IReadOnlyList<StockAnomaly>>.CreateSuccess(anomalies));
        })
            .WithName("DetectAnomalies")
            .WithTags("AI")
            .RequireAuthorization(CapabilityPolicies.View)
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

    private static IResult ForecastingUnavailable() => Results.Json(
        ApiResponse<object>.CreateFailure(
            "Demand forecasting is currently unavailable because it is disabled by server configuration."),
        statusCode: StatusCodes.Status503ServiceUnavailable);
}
