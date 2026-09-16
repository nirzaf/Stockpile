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
                OnTokenValidated = context =>
                {
                    var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
                    var tokenTenant = context.Principal?.FindFirst("tenant_id")?.Value;
                    if (!tenantContext.IsResolved ||
                        !string.Equals(tokenTenant, tenantContext.TenantId, StringComparison.Ordinal))
                    {
                        context.Fail("The token tenant does not match the request tenant.");
                    }

                    return Task.CompletedTask;
                }
            };
        });

        services.AddAuthorization(options =>
        {
            options.AddPolicy("Api", policy =>
            {
                policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
            });
        });

        return services;
    }
}
