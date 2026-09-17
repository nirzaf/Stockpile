using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Infrastructure.Data;
using Merconiq.Web.Security;
using Merconiq.Web.Tenancy;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;

namespace Merconiq.Web.Configuration;

public sealed record JwtTokenOptions(byte[] SigningKey, string Issuer, string Audience);

public static class IdentityExtensions
{
    public static IServiceCollection AddInventoryIdentityAndAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.Configure<TenantOptions>(
            configuration.GetSection(TenantOptions.SectionName));
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(serviceProvider =>
            serviceProvider.GetRequiredService<TenantContext>());
        services.AddSingleton<HostTenantResolver>();
        services.AddScoped<AdminBootstrapService>();
        services.AddScoped<CurrentUserAuthorization>();
        services.AddScoped<ICurrentUserAuthorization>(serviceProvider =>
            serviceProvider.GetRequiredService<CurrentUserAuthorization>());
        services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
            CompanyCapabilityAuthorizationHandler>();
        services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
            TenantAdministratorAuthorizationHandler>();

        services.AddIdentity<ApplicationUser, IdentityRole>(options =>
        {
            options.Password.RequireDigit = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireNonAlphanumeric = false;
            options.Password.RequiredLength = 8;
        })
        .AddEntityFrameworkStores<InventoryDbContext>()
        .AddDefaultTokenProviders();

        services.Configure<IdentityOptions>(options =>
        {
            options.Lockout.AllowedForNewUsers = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
        });

        services.AddScoped<IUserClaimsPrincipalFactory<ApplicationUser>, TenantClaimsPrincipalFactory>();

        services.ConfigureApplicationCookie(options =>
        {
            options.LoginPath = "/Account/Login";
            options.AccessDeniedPath = "/Account/AccessDenied";
            options.Events.OnValidatePrincipal = context =>
            {
                var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
                var principalTenant = context.Principal?.FindFirst("tenant_id")?.Value;
                if (!tenantContext.IsResolved ||
                    !string.Equals(principalTenant, tenantContext.TenantId, StringComparison.Ordinal))
                {
                    context.RejectPrincipal();
                    return Task.CompletedTask;
                }

                return SecurityStampValidator.ValidatePrincipalAsync(context);
            };
        });
        services.Configure<SecurityStampValidatorOptions>(options =>
            options.ValidationInterval = TimeSpan.FromMinutes(1));

        var jwtSettings = configuration.GetSection("JwtSettings");
        var secretKey = jwtSettings["Secret"];
        if (string.IsNullOrWhiteSpace(secretKey))
        {
            if (environment.IsEnvironment("Testing"))
            {
                secretKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            }
            else
            {
                throw new InvalidOperationException(
                    "JwtSettings:Secret must be configured through user secrets or the deployment environment.");
            }
        }

        if (Encoding.UTF8.GetByteCount(secretKey) < 32)
        {
            throw new InvalidOperationException("JwtSettings:Secret must be at least 32 bytes long.");
        }

        var tokenOptions = new JwtTokenOptions(
            Encoding.UTF8.GetBytes(secretKey),
            jwtSettings["Issuer"] ?? "Merconiq",
            jwtSettings["Audience"] ?? "Merconiq");
        services.AddSingleton(tokenOptions);

        services.AddAuthentication(options =>
        {
            options.DefaultScheme = IdentityConstants.ApplicationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.RequireHttpsMetadata = false;
            options.SaveToken = true;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(tokenOptions.SigningKey),
                ValidateIssuer = true,
                ValidIssuer = tokenOptions.Issuer,
                ValidateAudience = true,
                ValidAudience = tokenOptions.Audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = async context =>
                {
                    var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
                    var tokenTenant = context.Principal?.FindFirst("tenant_id")?.Value;
                    if (!tenantContext.IsResolved ||
                        !string.Equals(tokenTenant, tenantContext.TenantId, StringComparison.Ordinal))
                    {
                        context.Fail("The token tenant does not match the request tenant.");
                        return;
                    }

                    var identity = context.HttpContext.RequestServices.GetRequiredService<IOptions<IdentityOptions>>().Value;
                    var userId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                    var tokenSecurityStamp = context.Principal?.FindFirstValue(
                        identity.ClaimsIdentity.SecurityStampClaimType);
                    if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(tokenSecurityStamp))
                    {
                        context.Fail("The token does not contain the required user and security-stamp claims.");
                        return;
                    }

                    var userManager = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
                    var user = await userManager.FindByIdAsync(userId);
                    if (user is null || !string.Equals(user.TenantId, tenantContext.TenantId, StringComparison.Ordinal) ||
                        !string.Equals(tokenSecurityStamp,
                            await userManager.GetSecurityStampAsync(user), StringComparison.Ordinal))
                    {
                        context.Fail("The token user, tenant, or security stamp is no longer current.");
                    }
                }
            };
        });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(CapabilityPolicies.TenantAdministrator, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new TenantAdministratorRequirement()));
            options.AddPolicy("Api", policy =>
            {
                policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
            });
            options.AddPolicy(CapabilityPolicies.View, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new CompanyCapabilityRequirement(CompanyCapability.View)));
            options.AddPolicy(CapabilityPolicies.Edit, policy =>
                policy.RequireRole("Admin", "Manager", "Buyer", "CompanyAdmin")
                    .AddRequirements(new CompanyCapabilityRequirement(CompanyCapability.Edit)));
            options.AddPolicy(CapabilityPolicies.Approve, policy =>
                policy.RequireRole("Admin", "Accountant")
                    .AddRequirements(new CompanyCapabilityRequirement(CompanyCapability.Approve)));
            options.AddPolicy(CapabilityPolicies.Post, policy =>
                policy.RequireRole("Admin", "Manager", "Staff", "Operator", "Accountant", "Cashier")
                    .AddRequirements(new CompanyCapabilityRequirement(CompanyCapability.Post)));
            options.AddPolicy(CapabilityPolicies.Reverse, policy =>
                policy.RequireRole("Admin", "Accountant")
                    .AddRequirements(new CompanyCapabilityRequirement(CompanyCapability.Reverse)));
            options.AddPolicy(CapabilityPolicies.OverrideExpiredStock, policy =>
                policy.RequireRole("Admin", "Accountant")
                    .AddRequirements(new CompanyCapabilityRequirement(CompanyCapability.OverrideExpiredStock)));
            options.AddPolicy(CapabilityPolicies.OverrideQuarantinedStock, policy =>
                policy.RequireRole("Admin", "Accountant")
                    .AddRequirements(new CompanyCapabilityRequirement(CompanyCapability.OverrideQuarantinedStock)));
            options.AddPolicy(CapabilityPolicies.Administer, policy =>
                policy.RequireRole("Admin", "CompanyAdmin")
                    .AddRequirements(new CompanyCapabilityRequirement(CompanyCapability.Administer)));
        });

        return services;
    }
}
