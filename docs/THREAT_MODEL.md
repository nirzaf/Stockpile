# Merconiq threat model and tenant authorization matrix

This document describes the trust boundaries implemented in the current source. It is
an engineering review aid, not a penetration test, certification, or claim that Merconiq
is critical infrastructure.

## Assets and actors

The protected assets are tenant inventory, stock balances and transactions, purchase
orders, user identities and roles, webhook secrets, JWT signing material, cookie/data
protection keys, and operational logs. Actors are anonymous callers, authenticated
browser users, bearer-token API clients, configured hosts/proxies, the database, the
forecast worker, and the webhook worker.

The primary trust boundary is the configured host-to-tenant mapping. A request must first
resolve a tenant in `TenantContext`; cookie and bearer authentication then compare their
`tenant_id` claim with that context. The database applies tenant query filters and the
forecast cache keys include the tenant ID. Workers do not inherit an HTTP request: they
select a tenant explicitly before resolving tenant-scoped services.

## Authorization matrix

| Operation | Anonymous | Admin | Manager | Staff | Evidence and status |
| --- | --- | --- | --- | --- | --- |
| Item catalog reads and writes | Denied by `Api` policy | Allowed | Allowed | Allowed | `Merconiq.Web/Controllers/Api/V1/ItemsController.cs`; `Merconiq.Tests/Integration/TenantAuthenticationTests.cs`; implemented, API-tested |
| Stock/in-hand and transaction reads | Denied by `Api` policy | Allowed | Allowed | Allowed | `Merconiq.Web/Controllers/Api/V1/StockController.cs`; tenant query filters; implemented, existing API/tenant tests |
| Receive, transfer, and sell | Denied by `Api` policy | Allowed | Allowed | Allowed | `Merconiq.Web/Controllers/Api/V1/StockController.cs` role attributes; `Merconiq.Tests/Web/Controllers/TransferStockIdempotencyTests.cs` and `Merconiq.Tests/Web/Controllers/SellStockIdempotencyTests.cs`; implemented, unit-tested; PostgreSQL execution is separately recorded by CI |
| Webhook list/create/update/delete | Denied by `Api` policy | Allowed | Allowed | Denied | `Merconiq.Web/Configuration/EndpointExtensions.cs` role policies; `Merconiq.Tests/Infrastructure/WebhookDispatcherTests.cs`; implemented, service-tested |
| Forecasts and anomaly detection | Denied by `Api` policy | Allowed | Allowed | Allowed | `Merconiq.Web/Configuration/EndpointExtensions.cs` API group and tenant cache keys; `Merconiq.Tests/Integration/AiEndpointTests.cs`; implemented, API-tested |
| Browser login/logout | Login is public; logout requires a valid cookie | Authenticated user | Authenticated user | Authenticated user | `Merconiq.Web/Controllers/AccountController.cs`; antiforgery on browser mutations; implemented |
| Health and culture selection | Health is public; invalid culture is rejected | Public | Public | Public | `Merconiq.Web/Configuration/EndpointExtensions.cs`; operational endpoint, no tenant data |

The API uses bearer authentication through the `Api` policy. Browser cookie authentication
is used by MVC/Razor UI routes. The two surfaces must not be treated as interchangeable:
the bearer policy does not authorize from an ambient cookie, while browser mutations keep
ASP.NET Core antiforgery validation.

## Boundary evidence

### Tenant and authentication boundaries

`Merconiq.Web/Configuration/IdentityExtensions.cs` rejects a cookie or JWT whose `tenant_id` does not equal the resolved
host tenant, and rejects requests when no tenant is resolved. `Merconiq.Tests/Integration/TenantAuthenticationTests.cs`
covers wrong-host and mismatched-claim rejection. `Merconiq.Tests/Infrastructure/TenantIsolationTests.cs` and the filtered
`Merconiq.Infrastructure/Data/InventoryDbContext.cs` cover tenant-scoped reads; `Merconiq.Core/Interfaces/TenantCacheKeys.cs` is used by forecast
endpoints so equal item IDs in two tenants do not share a cache entry.

`auth/token` is intentionally anonymous only long enough to verify credentials. It still
requires a resolved host tenant and only issues a token for a user in that tenant. Login
abuse controls are documented in `SECURITY.md` and tested by the login-abuse issue/PR.

### Browser CSRF and bearer API behavior

`Merconiq.Web/Controllers/AccountController` retains `[ValidateAntiForgeryToken]` on login and logout. The stock
transfer and sell actions are bearer-only API actions; their explicit antiforgery marker
is accompanied by `[IgnoreAntiforgeryToken]` because no ambient cookie authenticates them.
This is a source-analysis annotation for the supported authentication boundary, not a
replacement for bearer validation. Any change to the authentication schemes must add an
integration test before changing this decision.

### Background workers

`Merconiq.Web/BackgroundServices/ForecastBackgroundService.cs` enumerates configured tenant IDs and invokes
`Merconiq.Web/BackgroundServices/TenantForecastRunner.cs`, which sets the tenant before creating the forecast service.
`Merconiq.Web/BackgroundServices/WebhookDeliveryBackgroundService.cs` loads the delivery and subscription using the delivery
tenant, sets the tenant context, then completes the record under that tenant. These paths
are isolated from HTTP middleware. `Merconiq.Tests/Integration/TenantBackgroundProcessingTests.cs` and
`Merconiq.Tests/Infrastructure/WebhookDispatcherTests.cs` provide executable evidence; production
multi-tenant scheduling and failed-worker recovery remain configured-not-exercised here.

### Webhooks and outbound network trust

`Merconiq.Web/Security/WebhookUrlValidator.cs` rejects unsupported schemes, loopback/private/link-local addresses
and unsafe DNS results before delivery. The validator is checked without sending requests
to private infrastructure. Redirect handling, DNS rebinding between validation and send,
and operator-approved public destination ownership are residual risks; they are not
claimed as fully verified by this document. Secrets are omitted from API responses, but
operators must still protect storage and logs.

## Status of claims

| Claim | Classification | Evidence or limitation |
| --- | --- | --- |
| Host, cookie, and JWT tenant agreement | Implemented-and-tested | `IdentityExtensions`, `TenantAuthenticationTests` |
| Tenant-filtered persistence and forecast cache separation | Implemented-and-tested | `InventoryDbContext`, `TenantIsolationTests`, cache-key tests |
| Role restrictions for webhook management and stock mutation | Implemented-and-tested | Endpoint/controller attributes and API tests |
| Background worker tenant selection | Implemented-and-tested in isolation | Worker tests use synthetic tenants; no production scheduler observation |
| Browser CSRF protection | Implemented-and-tested for MVC mutations | API transfer/sell remain bearer-only by contract |
| Redirect/DNS-rebinding resistance for outbound webhooks | Configured-not-exercised | No private-infrastructure requests were made |
| Production proxy/header configuration and real tenant inventory | Unresolved | Requires deployment-owner validation |

## Review checklist

When a new endpoint or worker is added, record its persona, tenant source, authentication
scheme, cache key, persistence filter, and test in this matrix. Do not infer security or
grant eligibility from the existence of this file. Real vulnerabilities belong in the
private disclosure channel described by `SECURITY.md`.
