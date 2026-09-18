# Public API contract

Merconiq exposes its public resource HTTP API through versioned MVC controllers at
`/api/v1`. Resource routes, authentication, validation, response envelopes, rate
limits, and OpenAPI metadata are defined on the controllers. Auth, webhook, and AI
operations remain minimal endpoints because they are not duplicate resource routes.

This document describes the HTTP contract implemented by Merconiq's current
`/api/v1` endpoints. It is intentionally limited to behavior present in the
application source; it does not describe a separate gateway, tenant header, or
external identity service.

## Versioning and transport

Resource endpoints are versioned MVC controllers under `/api/v1`. The API
versioning configuration also accepts `x-api-version`; the URL form is the
canonical form used by the routes and examples below. Responses use JSON.

The request host is part of the security boundary. The application resolves a
tenant before authentication by looking up `Request.Host.Host` in
`Tenancy:HostTenants`. Host matching is case-insensitive and ignores a trailing
dot. The mapped tenant identifier must be 64 characters or fewer and contain
only letters, digits, `-`, `_`, or `.`. A host that is not in the configured
allow-list is rejected with `400` before an endpoint runs.

There is no supported `Tenant-Id`, `X-Tenant-Id`, or similar request header.
Forwarded host information is not treated as a tenant selector by the resolver;
the trusted edge must route the request to a configured host. The default
configuration maps `localhost`, `127.0.0.1`, and `::1` to a tenant; deployments
must replace or extend those bindings for their own hosts.

## Authentication

### Obtain a bearer token

`POST /api/v1/auth/token` is the only anonymous API endpoint. Send the user name
or email and password in the host-bound tenant:

```http
POST /api/v1/auth/token
Host: tenant.example
Content-Type: application/json

{"username":"user@example.com","password":"Password1"}
```

A successful response is an unwrapped object containing a JWT and its expiry:

```json
{"token":"<jwt>","expires":"<utc timestamp>"}
```

The token is valid for two hours. Its issuer, audience, and signing key come
from `JwtSettings`; the secret must be at least 32 bytes. The token contains the
user name, user identifier, `tenant_id`, and distinct role claims. A user with
no stored roles receives the `Staff` role fallback.

Use the token on all other API calls:

```http
Authorization: Bearer <jwt>
```

API authorization explicitly uses the JWT bearer scheme. The `tenant_id` claim
must equal the tenant resolved from the request host. A missing, invalid,
expired, or wrong-tenant token receives `401 Unauthorized`. An authenticated
token that lacks an endpoint's required role receives `403 Forbidden`.

The application also has cookie authentication for the MVC UI. API callers
should use bearer tokens; cookie login paths and redirects are not part of the
API contract.

## Roles and routes

All routes below except token issuance require the `Api` policy (an authenticated
JWT). Routes may add role or capability checks as shown in the Authorization
column; reads without an additional restriction accept any valid API role.
`Admin`, `Manager`, and `Staff` are the roles accepted by stock mutations.
Webhook administration is restricted to `Admin` and `Manager`.

| Method | Route | Authorization | Success |
| --- | --- | --- | --- |
| POST | `/api/v1/auth/token` | Anonymous; host still selects the tenant | `200` |
| GET | `/api/v1/items?page=1&pageSize=25` | Any API JWT | `200` |
| GET | `/api/v1/items/{id}` | Any API JWT | `200` or `404` |
| GET | `/api/v1/items/search?q=...` | Any API JWT | `200` |
| POST | `/api/v1/items` | Any API JWT | `201` |
| PUT | `/api/v1/items/{id}` | Any API JWT | `204` |
| DELETE | `/api/v1/items/{id}` | Any API JWT | `204` |
| GET | `/api/v1/tax-rules` | Any API JWT | `200` |
| GET | `/api/v1/tax-rules/{id}` | Any API JWT | `200` or `404` |
| POST | `/api/v1/tax-rules` | Edit capability | `201` or `400` |
| GET | `/api/v1/stock/in-hand` | Any API JWT | `200` |
| GET | `/api/v1/stock/in-hand/{itemId}/{locationId}` | Any API JWT | `200` or `404` |
| GET | `/api/v1/stock/transactions` | Any API JWT | `200` |
| GET | `/api/v1/stock/valuation` | Any API JWT | `200` |
| POST | `/api/v1/stock/receive` | `Admin`, `Manager`, or `Staff` | `204` with an idempotency key; otherwise `400` |
| POST | `/api/v1/stock/transfer` | `Admin`, `Manager`, or `Staff` | `204` |
| POST | `/api/v1/stock/sell` | `Admin`, `Manager`, or `Staff` | `204` |
| POST | `/api/v1/stock/opening/preview` | Tenant `Admin` with `Approve` | `200` or `422` |
| POST | `/api/v1/stock/opening/replay` | Tenant `Admin` with `Approve` | `200` or `422` |
| POST | `/api/v1/stock/opening/reverse` | Tenant `Admin` with `Approve` and `Reverse` | `200` |
| GET | `/api/v1/webhooks` | `Admin` or `Manager` | `200` |
| GET | `/api/v1/webhooks/deliveries?page=1&pageSize=50` | Tenant `Admin` | `200` or `400` |
| POST | `/api/v1/webhooks` | `Admin` or `Manager` | `200` |
| PUT | `/api/v1/webhooks/{id}` | `Admin` or `Manager` | `200` or `404` |
| DELETE | `/api/v1/webhooks/{id}` | `Admin` or `Manager` | `204` or `404` |
| GET | `/api/v1/forecast/{itemId}` | Any API JWT; AI limit | `200` |
| GET | `/api/v1/forecast` | Any API JWT; AI limit | `200` |
| GET | `/api/v1/anomalies` | Any API JWT; AI limit | `200` |
| POST | `/api/v1/organization/companies/import` | Tenant `Admin` | `200` or `422` |
| POST | `/api/v1/organization/branches/import` | Company `Edit` | `200` or `422` |
| POST | `/api/v1/organization/locations/import` | Company `Administer` | `200` or `422` |
| POST | `/api/v1/organization/suppliers/import` | Company `Edit` | `200` or `422` |
| POST | `/api/v1/organization/units/import` | Tenant `Admin` plus `CompanyId` | `200` or `422` |
| GET | `/api/v1/organization/units/export` | Tenant `Admin` | `200` or `400` |
| POST | `/api/v1/organization/items/import` | Company `Edit` plus `CompanyId` | `200` or `422` |

### Tenant unit source-ID export

`GET /api/v1/organization/units/export` requires an API JWT and the tenant
`Admin` role. Units are tenant-scoped; this endpoint does not accept a company
scope.

| Query parameter | Default | Bounds / meaning |
| --- | --- | --- |
| `pageSize` | `50` | `1`–`100` records per response; values outside this range return `400`. |
| `afterExternalId` | omitted | Optional cursor, at most 128 characters. Pass the previous response's `nextCursor` unchanged to continue after that source ID; longer values return `400`. |

The response is wrapped in the standard API envelope. `units` are ordered by
`externalId` and expose only `externalId`, `code`, `name`, `decimalPlaces`, and
`isWholeUnitOnly`. `hasMore` indicates another page is available, and
`nextCursor` is the last returned `externalId` when more rows remain (otherwise
`null`). Units without a source ID—including legacy units assigned a synthetic
ID because their original source ID is unknown—are omitted.

```json
{
  "success": true,
  "data": {
    "pageSize": 1,
    "hasMore": true,
    "nextCursor": "unit-each",
    "units": [
      {
        "externalId": "unit-each",
        "code": "EA",
        "name": "Each",
        "decimalPlaces": 0,
        "isWholeUnitOnly": true
      }
    ]
  }
}
```

Forecast endpoints return `400 Bad Request` if the requested horizon, matching
historical sell-row count, or all-item catalog size exceeds its configured limit.
Over-limit forecasts are rejected, never silently truncated; see
[`FORECASTING_RUNTIME.md`](FORECASTING_RUNTIME.md) for defaults and supported
configuration ceilings.

Opening baseline endpoints accept an explicit-cost CSV with the header
`external_reference,item_external_id,location_id,quantity,unit_cost`. Preview is
read-only and reports current-versus-approved unbatched quantities. Replay uses a
stable import reference and approval reference, defaults the cutover to the
server's current UTC instant when omitted, and creates one immutable opening
movement and valuation entry per valid source row. A tenant can have one approved
baseline. Reversal requires a distinct correction and approval reference plus a
reason; it posts forward stock effects and never edits the original baseline.
Missing cost is rejected, while explicit zero cost is accepted. The M06 opening
journal and financial activation gate are outside this API slice.

## Outbound webhooks

Webhook deliveries are at-least-once: a receiver can see the same delivery again
after a timeout or worker restart. `X-Inventory-Event-Id` is the stable event
UUID and matches `EventId` in the JSON envelope; deduplicate business effects by
this value. Retries and deliveries to multiple subscriptions retain that event
identity. The sender does not expose its internal per-subscription delivery-row
ID in request headers.

When a non-empty subscription secret is configured, the sender adds
`X-Inventory-Signature`: lower-case hexadecimal HMAC-SHA256 of the exact UTF-8
JSON request-body bytes, using the subscription secret's UTF-8 bytes as the
key. HTTP headers are not part of the signed value. Verify the raw request-body
bytes before parsing or reserializing JSON, and fail closed on a missing,
malformed, or mismatched signature. A receiver that requires authenticity must
use a non-empty secret. The webhook administration API never returns the secret.

Example receiver-side verification in .NET:

```csharp
using System;
using System.Security.Cryptography;
using System.Text;

static bool HasValidWebhookSignature(
    ReadOnlySpan<byte> rawBody,
    string secret,
    string? signatureHeader)
{
    if (string.IsNullOrEmpty(secret) || signatureHeader is null || signatureHeader.Length != 64)
        return false;

    Span<byte> supplied = stackalloc byte[32];
    if (!Convert.TryFromHexString(signatureHeader, supplied, out var written) || written != supplied.Length)
        return false;

    Span<byte> expected = stackalloc byte[32];
    HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), rawBody, expected);
    return CryptographicOperations.FixedTimeEquals(expected, supplied);
}
```

After signature verification, parse the envelope and use its `EventId` for
idempotent event handling. Do not log the secret, signature, or unredacted
payload.

Webhook targets must be HTTPS on port 443 and resolve only to public addresses.
The delivery worker retries transient failures with bounded exponential delay,
up to five total attempts; permanent client errors are dead-lettered immediately.
Operational response diagnostics are limited to 4 KiB and redact the configured
secret and full target URL; transport errors are recorded without exception text
or endpoint details.
PostgreSQL workers claim due deliveries atomically with `SKIP LOCKED` and a
two-minute lease. An expired in-progress lease can be reclaimed after a worker
crash; a per-claim token prevents a superseded worker from persisting a stale
completion. This protects outbox ownership, but does not make delivery exactly
once: receivers must still deduplicate repeated HTTP requests by event ID.
No remote endpoint is contacted unless an administrator explicitly configures
the subscription.

Tenant administrators can inspect recent delivery metadata at
`GET /api/v1/webhooks/deliveries?page=1&pageSize=50`. Pages are returned newest
first; `pageSize` is limited to 100 and the response includes `hasMore`. The
endpoint uses the active tenant filter and returns only delivery IDs, event type,
status, attempt timing and HTTP status. Its `failureReason` is normally `null`;
the only exposed reason is the fixed message
`Serialized webhook event envelope exceeds the 256 KiB UTF-8 limit.` for an
oversized persisted event. Arbitrary diagnostic text is never returned. The
endpoint never returns payloads, target URLs, subscription secrets, lease
tokens or raw response bodies.

Serialized outbound webhook event envelopes are limited to 256 KiB (262,144
UTF-8 bytes). Enqueueing rejects a larger envelope before creating any outbox
rows; the payload is never truncated. A persisted oversized record—including
one created before this limit was enforced—is not sent: the worker marks it
dead-lettered and records the fixed safe reason shown above. Direct dispatch
also rejects an oversized envelope before contacting any endpoint.

The item list validates `page >= 1` and `1 <= pageSize <= 100`. Item update
also requires the route ID and body ID to match. Request validation failures
are `400` responses.

## Tax rules and deterministic amounts

Tax rules are tenant-scoped, effective-dated policies. `EffectiveFromUtc` is
inclusive and `EffectiveToUtc` is exclusive; periods for one code cannot
overlap. Create a new dated rule instead of editing a rule used by a document:

```json
{
  "code": "STANDARD-15",
  "category": "Standard",
  "ratePercent": 15,
  "calculationMode": "Exclusive",
  "effectiveFromUtc": "2026-01-01T00:00:00Z",
  "effectiveToUtc": null,
  "isActive": true
}
```

Purchase-order creation calculates on the server. The shared policy rounds
quantity × unit price, then the discount, taxable base, tax, and gross amount
at the document currency scale using midpoint-away-from-zero. Inclusive tax is
extracted from the rounded post-discount gross amount. Standard, zero-rated and
exempt categories are supported; zero-rated and exempt rules must use a zero
rate. Unsupported tax combinations are rejected rather than inferred.

The selected rule ID, effective date, category, rate, discount, calculation
version, rounded line amounts and document totals are stored as snapshots.
Changing a tax master therefore cannot recalculate an existing document.
These rules are configuration primitives and synthetic examples, not
jurisdictional tax or compliance certification.

## Response envelopes

Successful resource responses normally use `ApiResponse<T>`:

```json
{"success":true,"data":{},"errorMessage":null,"errors":null}
```

Application-level failures that use the same type have this shape:

```json
{"success":false,"data":null,"errorMessage":"...","errors":["..."]}
```

The following endpoints intentionally do not use that envelope:

- token issuance returns `{token, expires}`;
- `204 No Content` operations have an empty body;
- unmapped hosts return the tenant middleware's `{title, detail, status}` body;
- API exceptions and validation failures use RFC 7807 `ProblemDetails` or
  `ValidationProblemDetails` (`application/problem+json`), with `title`,
  `detail`, `status`, and `instance` as applicable;
- authentication challenges and rate-limit rejections are framework responses
  and should be handled by status code rather than by assuming an application
  envelope.

## Status codes

| Status | Meaning in this API |
| --- | --- |
| `200` | Successful read, token issuance, webhook mutation, forecast, or anomaly response |
| `201` | Item created; the `Location` header identifies the item resource |
| `204` | Successful stock mutation, item update/delete, or webhook delete; no body |
| `400` | Unmapped host, invalid query/body, validation failure, route/body ID mismatch, a missing required `Idempotency-Key`, or an `Idempotency-Key` longer than 200 characters |
| `401` | Missing/invalid/expired bearer token, failed credentials, or token tenant mismatch |
| `403` | Authenticated caller lacks the required role |
| `404` | Requested item, stock balance, or webhook subscription does not exist |
| `409` | Business conflict or reuse of an idempotency key with a different request |
| `429` | Fixed-window rate limit rejected the request after the configured queue is full |
| `500` | Unexpected API exception; the server logs the exception and returns a stable problem response |

## Rate limits

The normal `Api` policy is a fixed window of 100 permits per minute with a
queue of 10. Its partition is `tenant-id:sha256(client-discriminator)`, so the
same client is isolated between tenants. Authentication runs before this
limiter, and the client discriminator is selected from the authenticated name
identifier, the authenticated `client_id` claim, the remote IP address, or
`anonymous`, in that order. Caller-supplied `X-Client-Id` headers are not used
for rate-limit partitioning because they can be rotated to evade a limit; they
never select a tenant.

AI routes use the explicit `Ai` policy: 10 permits per minute with a queue of
2. A rejected request receives `429 Too Many Requests`; the application does
not configure a `Retry-After` header.

## Idempotent stock mutations

`POST /api/v1/stock/receive` requires the `Idempotency-Key` request header;
missing or over-200-character keys are rejected with `400` before posting.
`POST /api/v1/stock/transfer`, `/api/v1/stock/sell`,
`/api/v1/stock/quarantine`, and `/api/v1/stock/quarantine/release` accept the
header optionally; when supplied, it must be 200 characters or fewer. Those
commands retain their normal one-shot path when the header is omitted.

For these stock routes, retry the same supplied key with the same request body
to replay a completed operation. Receive always requires a key; the other
listed routes retain their one-shot path when the header is omitted. The durable
coordinator scopes the claim to the current tenant, HTTP method/path, key, and
SHA-256 hash of the serialized command; it retains claims for one hour with a
two-minute lease.

```http
POST /api/v1/stock/receive
Host: tenant.example
Authorization: Bearer <jwt>
Idempotency-Key: receive-2026-09-16-001
Content-Type: application/json

{"itemId":42,"locationId":7,"quantity":10,"unitCost":12.50,"notes":"delivery"}
```

`unitCost` is optional and is the acquisition cost per base unit. When present,
the receipt contributes to the tenant/item/location moving-average valuation and
must be non-negative with at most six decimal places. Costed receipts are
unbatched in this release; omitted `unitCost` preserves quantity-only behavior.

`GET /api/v1/stock/valuation` returns each current tenant/item/location moving-average
bucket and its ordered immutable valuation entries from one repeatable-read
snapshot, so each returned balance reconciles with its ledger at the time of the
read. Optional `itemId` and
`locationId` filters are applied within the caller's authorized company scope;
the endpoint is read-only and never recalculates or mutates posted costs.

The state transitions are:

- The first request claims the key, executes the stock operation and completion
  record in the same transaction, and returns `204`.
- A later request with the same tenant, route, key, and request body is a
  replay. The stored operation is not executed again and the endpoint returns
  `204` with no body.
- Reusing a key with a different command hash is a conflict and returns `409`
  as an API `ProblemDetails` response. Use a new key for a different command.
- A failed operation is recorded as failed and may be retried with the same
  key and hash. Business changes and the successful completion record are not
  committed from the failed attempt.
- While another request owns a live lease, the caller waits for the claim and
  its cancellation token is honored. The wait is bounded by 30 seconds; a
  cancellation aborts the wait before the operation runs.
- Once the operation has been claimed, transaction finalization uses an
  independent non-cancelable token so an already-started operation can finish
  and record completion. The transfer and sell command delegates still receive
  the request cancellation token; if their operation observes cancellation and
  fails, the failed claim can be retried.

Existing executable coverage for this contract is kept in
`tests/Merconiq.Tests/Web/Services/IdempotencyKeyStoreTests.cs`,
`tests/Merconiq.Tests/Integration/TenantAuthenticationTests.cs`,
and `tests/Merconiq.Tests/Web/Services/RateLimitPartitionKeyTests.cs`.
The integration test factory uses an in-memory database and synthetic signed
JWTs; it does not represent an external identity provider or a production rate
limit measurement.

## Transfer-order dispatch

Dispatch an approved transfer-order line with company-scoped `Post` permission
and a required `Idempotency-Key`:

```http
POST /api/v1/transfer-orders/123/lines/456/dispatch
Host: tenant.example
Authorization: Bearer <jwt>
Idempotency-Key: dispatch-2026-09-17-001
Content-Type: application/json

{"quantity":30}
```

The response identifies the transfer line, source/destination, stock movement,
captured unit cost/value, dispatcher, and dispatch time. Replaying the same key
and command returns the original dispatch record; using that key with a different
quantity is rejected. Dispatch consumes only the approved outstanding
reservation, moves the source carrying value into the company transit ledger,
and does not increase destination stock. Once any quantity is dispatched, the
transfer order cannot be amended or cancelled.

This increment accepts only valued, unbatched source stock. Lot/expiry and
quantity-only stock are rejected because current M03 valuation does not provide
lot-level or historical acquisition-cost evidence.

Resolve a dispatched transit entry with company-scoped `Post` permission and a
required `Idempotency-Key`:

```http
POST /api/v1/transfer-orders/123/lines/456/transit/789/receive
Idempotency-Key: receive-2026-09-17-001
Content-Type: application/json

{"quantity":20,"notes":"Received in good condition"}
```

Use the same route with `/quarantine` (a non-blank `reason` is required) or
`/return`. Receipt and quarantine increase destination on-hand quantity; only
quarantined quantity is unavailable for further stock operations. Quarantine
is a custody step, not an approved write-off or final disposition; quarantined
goods remain valued stock until the separately approved M03 disposition
workflow is used. Returns move the captured transit value back to the source.
Partial settlement allocates the transit entry's captured total value, with the
final settlement receiving any rounding remainder so the ledger conserves the
dispatched value. Each settlement is append-only, lineage-linked, limited to
the unsettled dispatched quantity, and idempotent. The order reports
`PartiallyReceived` only after destination receipt; it becomes `Completed` when
all ordered quantity has been dispatched and every dispatched unit has been
received, quarantined, or returned. Lot/expiry inputs must match the dispatched
entry.
