using System.Threading.RateLimiting;
using Asp.Versioning;
using Merconiq.Core.Diagnostics;
using Merconiq.Infrastructure.Data;
using Merconiq.Web.Security;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi;
using MudBlazor.Services;

namespace Merconiq.Web.Configuration;

public static class PresentationExtensions
{
    public static IServiceCollection AddInventoryPresentation(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddMudServices();
        services.AddLocalization(options => options.ResourcesPath = "Resources");

        var allowedOrigins = configuration
            .GetSection("Cors:AllowedOrigins")
            .Get<string[]>() ?? [];
        services.AddCors(options =>
        {
            options.AddPolicy("Default", policy =>
            {
                if (allowedOrigins.Length > 0)
                {
                    policy.WithOrigins(allowedOrigins);
                }

                policy.AllowAnyHeader().AllowAnyMethod();
            });
        });

        services.AddControllersWithViews();
        services.AddRazorPages();
        services.AddRazorComponents().AddInteractiveServerComponents();

        services.AddApiVersioning(options =>
        {
            options.DefaultApiVersion = new ApiVersion(1, 0);
            options.AssumeDefaultVersionWhenUnspecified = true;
            options.ReportApiVersions = true;
            options.ApiVersionReader = ApiVersionReader.Combine(
                new UrlSegmentApiVersionReader(),
                new HeaderApiVersionReader("x-api-version"));
        }).AddMvc().AddApiExplorer(options =>
        {
            options.GroupNameFormat = "'v'VVV";
            options.SubstituteApiVersionInUrl = true;
        });

        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Merconiq API",
                Version = "v1",
                Description = "Merconiq API for inventory, stock, procurement, forecasting, webhooks, and business management capabilities"
            });

            options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Name = "Authorization",
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "JWT Authorization header using the Bearer scheme. Example: \"Bearer {token}\""
            });

            options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", document)] = []
            });

            foreach (var xmlFile in Directory.GetFiles(
                         AppContext.BaseDirectory, "*.xml", SearchOption.TopDirectoryOnly))
            {
                options.IncludeXmlComments(xmlFile);
            }
        });

        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddProblemDetails();
        services.AddResponseCompression(options => options.EnableForHttps = true);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("Api", httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    RateLimitPartitionKey.ForApi(httpContext),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 100,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 10
                    }));
            options.AddPolicy("Login", httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    RateLimitPartitionKey.ForLogin(httpContext),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 20,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
            options.AddFixedWindowLimiter("Ai", limiter =>
            {
                limiter.PermitLimit = 10;
                limiter.Window = TimeSpan.FromMinutes(1);
                limiter.QueueLimit = 2;
            });
        });

        services.AddHealthChecks().AddDbContextCheck<InventoryDbContext>();
        return services;
    }
}
