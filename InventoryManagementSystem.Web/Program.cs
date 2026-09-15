using System.Threading.RateLimiting;
using Asp.Versioning;
using FluentValidation;
using InventoryManagementSystem.Core.Behaviors;
using InventoryManagementSystem.Core.Features.Items.Queries;
using InventoryManagementSystem.Core.Features.Stock.Commands;
using InventoryManagementSystem.Core.Features.Stock.Queries;
using InventoryManagementSystem.Core.Interfaces;
using InventoryManagementSystem.Core.Services;
using InventoryManagementSystem.Core.Validators;
using InventoryManagementSystem.Infrastructure.Data;
using InventoryManagementSystem.Infrastructure.Repositories;
using InventoryManagementSystem.Web.Middleware;
using InventoryManagementSystem.Web.Services;
using InventoryManagementSystem.Web.Tenancy;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using MudBlazor.Services;
using Serilog;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using InventoryManagementSystem.Web.BackgroundServices;
using InventoryManagementSystem.Core.Entities;
using InventoryManagementSystem.Core.Models;
using InventoryManagementSystem.Core.Options;
using InventoryManagementSystem.Core.Diagnostics;
using InventoryManagementSystem.Web.Security;
using OpenTelemetry.Metrics;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace InventoryManagementSystem.Web;

/// <summary>
/// Application entry point. Wires up services (DI, auth, rate limiting, Swagger, MudBlazor,
/// MediatR, EF Core, Identity) and configures the HTTP request pipeline (middleware order:
/// Serilog → global exception handler → rate limiting → auth → endpoints).
/// </summary>
public class Program
{
    /// <summary>Builds, configures, and runs the ASP.NET Core web application.</summary>
    /// <param name="args">Command-line arguments passed to the host.</param>
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Serilog
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .WriteTo.File("logs/inventory-.txt", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        builder.Host.UseSerilog();

        var telemetryEnabled = builder.Configuration.GetValue<bool>("OpenTelemetry:Enabled");
        if (telemetryEnabled)
        {
            var openTelemetry = builder.Services.AddOpenTelemetry()
                .ConfigureResource(resource => resource.AddService("Stockpile"))
                .WithTracing(tracing => tracing
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddEntityFrameworkCoreInstrumentation())
                .WithMetrics(metrics => metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter(InventoryTelemetry.MeterName));

            var telemetryEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
            if (!string.IsNullOrWhiteSpace(telemetryEndpoint))
            {
                openTelemetry.UseOtlpExporter(OtlpExportProtocol.HttpProtobuf, new Uri(telemetryEndpoint));
            }
        }

        // Production cookie and token protection keys must survive container replacement and
        // be shared by replicas. Development and Testing retain the framework's self-contained
        // key repositories so local runs do not require external storage.
        builder.Services.AddOptions<KeyStorageOptions>()
            .Bind(builder.Configuration.GetSection(KeyStorageOptions.SectionName));
        var dataProtection = builder.Services.AddDataProtection()
            .SetApplicationName("Stockpile");
        if (builder.Environment.IsProduction())
        {
            var keyPath = builder.Configuration
                .GetSection(KeyStorageOptions.SectionName)
                .GetValue<string>(nameof(KeyStorageOptions.KeyStoragePath))
                ?? "/app/data/keys";
            Directory.CreateDirectory(keyPath);
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keyPath));
        }

        // Database (skip PostgreSQL in Testing — replaced by InMemory in test factory)
        if (!builder.Environment.IsEnvironment("Testing"))
        {
            builder.Services.AddDbContext<InventoryDbContext>(options =>
                options.UseNpgsql(
                    builder.Configuration.GetConnectionString("DefaultConnection"),
                    npgsql => npgsql.EnableRetryOnFailure(
                        maxRetryCount: 3,
                        maxRetryDelay: TimeSpan.FromSeconds(30),
                        errorCodesToAdd: null)));
        }

        builder.Services.AddDatabaseDeveloperPageExceptionFilter();

        // Tenant resolution must run before authentication so Identity and EF use the same
        // trusted host binding. Tenant claims are supplemental and are handled separately.
        builder.Services.Configure<TenantOptions>(
            builder.Configuration.GetSection(TenantOptions.SectionName));
        builder.Services.AddScoped<TenantContext>();
        builder.Services.AddScoped<ITenantContext>(services =>
            services.GetRequiredService<TenantContext>());
        builder.Services.AddSingleton<HostTenantResolver>();

        // Identity
        builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
        {
            options.Password.RequireDigit = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireNonAlphanumeric = false;
            options.Password.RequiredLength = 8;
        })
        .AddEntityFrameworkStores<InventoryDbContext>()
        .AddDefaultTokenProviders();

        builder.Services.AddScoped<IUserClaimsPrincipalFactory<ApplicationUser>, TenantClaimsPrincipalFactory>();

        builder.Services.ConfigureApplicationCookie(options =>
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
                }

                return Task.CompletedTask;
            };
        });

        // Authentication Configuration (Cookies + JWT Bearer)
        // Two schemes are registered because the application has two distinct clients:
        //   * The MVC UI uses the Identity cookie scheme so browser navigations stay
        //     logged in across page loads.
        //   * The versioned /api/v1 endpoints use the JWT bearer scheme because they
        //     are consumed by scripts, mobile apps, and integration partners that do
        //     not share a browser cookie jar.
        // DefaultScheme resolves the scheme for the current request based on the
        // [Authorize] policy and the request path; the controllers explicitly call
        // [Authorize(AuthenticationSchemes = "Bearer")] on the API controllers.
        var jwtSettings = builder.Configuration.GetSection("JwtSettings");
        var secretKey = jwtSettings["Secret"];
        if (string.IsNullOrWhiteSpace(secretKey))
        {
            if (builder.Environment.IsEnvironment("Testing"))
            {
                secretKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
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

        var key = Encoding.UTF8.GetBytes(secretKey);

        builder.Services.AddAuthentication(options =>
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
                IssuerSigningKey = new SymmetricSecurityKey(key),
                ValidateIssuer = true,
                ValidIssuer = jwtSettings["Issuer"] ?? "InventoryManagementSystem",
                ValidateAudience = true,
                ValidAudience = jwtSettings["Audience"] ?? "InventoryManagementSystem",
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

        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("Api", policy =>
            {
                policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
            });
        });

        // MudBlazor
        builder.Services.AddMudServices();

        // UI localization. Query-string and cookie providers allow the interactive UI
        // to switch cultures without changing the authenticated session.
        builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

        // Caching
        builder.Services.AddMemoryCache();

        // HTTP context accessor for audit fields
        builder.Services.AddHttpContextAccessor();

        // Repositories
        builder.Services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
        builder.Services.AddScoped<IItemRepository, ItemRepository>();

        // Unit of Work — single commit boundary for multi-repository operations
        builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();

        // Services
        builder.Services.AddScoped<IItemService, ItemService>();
        builder.Services.AddScoped<ISupplierService, SupplierService>();
        builder.Services.AddScoped<ILocationService, LocationService>();
        builder.Services.AddScoped<IStockService, StockService>();
        builder.Services.AddScoped<IPurchaseOrderService, PurchaseOrderService>();

        // AI / ML.NET services (platform-independent, no Azure)
        builder.Services.AddOptions<ForecastingOptions>()
            .Bind(builder.Configuration.GetSection(ForecastingOptions.SectionName))
            .Validate(options => ForecastingImplementations.IsSupported(options.Implementation),
                $"{ForecastingOptions.SectionName}:Implementation must be '{ForecastingImplementations.ManagedMovingAverage}' or '{ForecastingImplementations.Ssa}'.")
            .ValidateOnStart();
        builder.Services.AddScoped<IDemandForecastService, DemandForecastService>();
        builder.Services.AddScoped<IAnomalyDetectionService, AnomalyDetectionService>();

        // HttpClient Factory & Webhooks
        builder.Services.AddHttpClient("Webhooks", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            // Redirects can move an otherwise safe URL into a private network.
            AllowAutoRedirect = false
        });
        builder.Services.AddScoped<IWebhookDispatcher, InventoryManagementSystem.Infrastructure.Services.WebhookDispatcher>();
        builder.Services.AddScoped<IIdempotencyKeyStore, IdempotencyKeyStore>();

        // FluentValidation — auto-validates MediatR requests via pipeline behavior
        builder.Services.AddValidatorsFromAssemblyContaining<ItemValidator>();
        builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        // MediatR CQRS — scans all handler assemblies
        // Assembly scanning starts from a concrete query type (GetAllItemsQuery) because
        // it lives in the same assembly as every command, query, and handler. A single
        // RegisterServicesFromAssembly call picks them all up, so adding a new handler
        // is a no-op registration-wise.
        builder.Services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(InventoryManagementSystem.Core.Features.Items.Queries.GetAllItemsQuery).Assembly));

        // CORS policy. Origins are configuration-driven so deployments can expose the API
        // only to their own trusted browser clients. An empty list intentionally disables
        // cross-origin browser access while preserving same-origin requests.
        var allowedOrigins = builder.Configuration
            .GetSection("Cors:AllowedOrigins")
            .Get<string[]>() ?? [];

        builder.Services.AddCors(options =>
        {
            options.AddPolicy("Default", policy =>
            {
                if (allowedOrigins.Length > 0)
                {
                    policy.WithOrigins(allowedOrigins);
                }

                policy.AllowAnyHeader()
                      .AllowAnyMethod();
            });
        });

        // MVC & Blazor
        builder.Services.AddControllersWithViews();
        builder.Services.AddRazorPages();
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        // API Versioning
        builder.Services.AddApiVersioning(options =>
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

        // Swagger/OpenAPI
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Inventory Management System API",
                Version = "v1",
                Description = "Commercial-grade API endpoints for Inventory Management System"
            });

            c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Name = "Authorization",
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "JWT Authorization header using the Bearer scheme. Example: \"Bearer {token}\""
            });

            c.AddSecurityRequirement(document => new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", document)] = []
            });

            // Include XML doc comments from all project assemblies so Swagger UI surfaces
            // the <summary>, <param>, and <returns> tags written in source.
            var xmlFiles = Directory.GetFiles(AppContext.BaseDirectory, "*.xml", SearchOption.TopDirectoryOnly);
            foreach (var xmlFile in xmlFiles)
            {
                c.IncludeXmlComments(xmlFile);
            }
        });

        // Global exception handling
        builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
        builder.Services.AddProblemDetails();

        // Background services
        builder.Services.AddSingleton<ITenantForecastRunner, TenantForecastRunner>();
        builder.Services.AddHostedService<ForecastBackgroundService>();
        builder.Services.AddHostedService<InventoryManagementSystem.Web.BackgroundServices.WebhookDeliveryBackgroundService>();

        // Response Compression
        builder.Services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
        });

        // Rate limiting
        // API callers get independent fixed windows per tenant and client. AI remains a
        // deliberately global fixed window so a fleet of callers cannot overwhelm CPU.
        // Two policies are defined because the cost profiles are very
        // different:
        //   * "Api" — 100 req/min covers the standard tier of CRUD traffic and
        //     protects the database from accidental or malicious bursts. 10-request
        //     queue gives clients a short grace period before they see 429s.
        //   * "Ai" — 10 req/min reflects that ML.NET forecasting and anomaly
        //     detection are CPU-bound and can dominate a single core for hundreds
        //     of milliseconds; the lower limit preserves headroom for other
        //     requests on the same host.
        builder.Services.AddRateLimiter(options =>
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
            // AI endpoints: 10 requests/minute (CPU-intensive ML.NET)
            options.AddFixedWindowLimiter("Ai", limiter =>
            {
                limiter.PermitLimit = 10;
                limiter.Window = TimeSpan.FromMinutes(1);
                limiter.QueueLimit = 2;
            });
        });

        // Health checks
        builder.Services.AddHealthChecks()
            .AddDbContextCheck<InventoryDbContext>();

        var app = builder.Build();

        var supportedCultures = new[] { "en-US", "ar-SA" };
        var localizationOptions = new RequestLocalizationOptions()
            .SetDefaultCulture(supportedCultures[0])
            .AddSupportedCultures(supportedCultures)
            .AddSupportedUICultures(supportedCultures);
        app.UseRequestLocalization(localizationOptions);

        app.UseResponseCompression();

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "Inventory API v1");
            });
        }

        if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
        {
            app.UseExceptionHandler("/Home/Error");

            // HSTS only when HTTPS is enabled (skip in Docker/reverse-proxy setups)
            if (string.IsNullOrEmpty(builder.Configuration["DISABLE_HTTPS"]))
            {
                app.UseHsts();
            }
        }
        else
        {
            // Still add exception handler middleware (without redirect path) to invoke IExceptionHandlers
            app.UseExceptionHandler(new ExceptionHandlerOptions { AllowStatusCode404Response = true });
        }

        // Skip HTTPS redirection in Docker or reverse-proxy deployments
        if (string.IsNullOrEmpty(builder.Configuration["DISABLE_HTTPS"]))
        {
            app.UseHttpsRedirection();
        }
        app.UseStaticFiles();
        app.UseSecurityHeaders();
        app.UseSerilogRequestLogging();
        app.UseTenantContext();

        app.UseRouting();
        app.UseCors("Default");
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();

        app.MapGet("/culture/set", (HttpContext httpContext, string culture, string? returnUrl) =>
        {
            var selectedCulture = supportedCultures.FirstOrDefault(
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

        // Minimal APIs are the single public resource style. The attribute-routed controller
        // classes remain internal compatibility code and are intentionally not mapped here.
        var v1 = app.MapGroup("/api/v1")
            .WithTags("API v1")
            .RequireAuthorization("Api")
            .RequireRateLimiting("Api");

        v1.MapPost("/auth/token", async (
            TokenRequest req,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            ITenantContext tenantContext) =>
        {
            var user = await userManager.FindByNameAsync(req.Username) ?? await userManager.FindByEmailAsync(req.Username);
            if (user == null) return Results.Unauthorized();

            if (!tenantContext.IsResolved ||
                !string.Equals(user.TenantId, tenantContext.TenantId, StringComparison.Ordinal))
            {
                return Results.Unauthorized();
            }

            var result = await signInManager.CheckPasswordSignInAsync(user, req.Password, false);
            if (!result.Succeeded) return Results.Unauthorized();

            var tokenHandler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
            var roles = await userManager.GetRolesAsync(user);
            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new System.Security.Claims.ClaimsIdentity(
                    JwtClaimsFactory.Create(user, tenantContext.TenantId, roles)),
                Expires = DateTime.UtcNow.AddHours(2),
                Issuer = jwtSettings["Issuer"] ?? "InventoryManagementSystem",
                Audience = jwtSettings["Audience"] ?? "InventoryManagementSystem",
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
            };
            var token = tokenHandler.CreateToken(tokenDescriptor);
            return Results.Ok(new { Token = tokenHandler.WriteToken(token), Expires = tokenDescriptor.Expires });
        })
        .AllowAnonymous()
        .WithName("GenerateToken")
        .WithTags("Auth");

        static async Task<string?> ValidateWebhookRequestAsync(WebhookSubscriptionRequest request)
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

        static WebhookSubscriptionResponse ToWebhookResponse(WebhookSubscription subscription) =>
            new(subscription.Id, subscription.Url, subscription.EventType, subscription.IsActive);

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

        // === AI / ML endpoints ===

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

        // Auto-apply EF Core migrations (Development only)
        if (app.Environment.IsDevelopment())
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            await db.Database.MigrateAsync();
        }

        // Seed demo data in development only
        if (app.Environment.IsDevelopment())
        {
            await SeedData.Initialize(app.Services);
        }

        await app.RunAsync();
    }
}

public record TokenRequest(string Username, string Password);

public record WebhookSubscriptionRequest(string Url, string EventType, string? Secret, bool IsActive = true);

public record WebhookSubscriptionResponse(int Id, string Url, string EventType, bool IsActive);
