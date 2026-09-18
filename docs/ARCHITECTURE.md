# Merconiq architecture and extension boundaries

Merconiq is currently one deployable ASP.NET Core application backed by
PostgreSQL. Business capabilities are grouped in the existing projects; they
are not separate deployable services or independently versioned packages. This
page describes boundaries demonstrated by the current source tree, not a
promise that every listed interface is a supported third-party API.

## Current ownership

| Capability | Current code ownership | Current boundary |
| --- | --- | --- |
| Platform, tenancy, authorization and audit | Contracts and entities in `src/Merconiq.Core`; HTTP authentication, authorization, tenant request context and host composition in `src/Merconiq.Web`; persistence and company membership implementation in `src/Merconiq.Infrastructure` | The Web host authenticates and authorizes requests. Tenant-scoped application services and persistence enforce data scope. Audit records are produced by the shared persistence pipeline. A service interface alone is not an authorization boundary. |
| Inventory | Domain entities, service contracts and stock operations in `src/Merconiq.Core`; PostgreSQL mapping and stock-count persistence in `src/Merconiq.Infrastructure`; HTTP/UI entry points in `src/Merconiq.Web` | Covers item/location stock, movement history, reservations, quarantine, valuation, stock counts and transfer-in-transit records. Inventory posting remains behind existing application services and their transaction/unit-of-work behavior. |
| Procurement | Supplier and purchase-order contracts, entities and services in `src/Merconiq.Core`; EF mapping/repositories in `src/Merconiq.Infrastructure`; endpoint/UI and DI composition in `src/Merconiq.Web` | An approved purchase order is a procurement document; it is not itself a stock receipt or a posted financial journal. Do not infer a goods-receipt-to-ledger contract that the current source does not implement. |
| Integrations and decision support | Contracts and forecasting services in `src/Merconiq.Core`; webhook delivery, configuration, host services and persistence registrations in `src/Merconiq.Infrastructure` and `src/Merconiq.Web` | Webhooks and forecast/anomaly services are current capabilities. Their presence does not make arbitrary implementations, payloads, or delivery behavior stable external contracts. See the open integration work in [issue #355](https://github.com/nirzaf/merconiq/issues/355). |
| General ledger / finance | No implemented general-ledger module is present in the current persistence model | Stock valuation records are not journal entries and must not be presented as a general ledger, accounts payable, or financial-statement source of truth. Finance remains future work. |

These are capability ownership notes, not a claim that code is already split
into module assemblies. New behavior should stay with the capability that owns
its invariants. Shared platform concerns belong in existing platform code;
they should not become an unowned cross-module utility layer.

## Allowed dependency direction

```text
Merconiq.Core <- Merconiq.Infrastructure <- Merconiq.Web
       ^                  ^                    ^
       +--------------- Merconiq.Tests --------+
```

- `Merconiq.Core` has no production project references. It owns domain types,
  service contracts and persistence-agnostic application/domain logic.
- `Merconiq.Infrastructure` references Core only. It owns EF Core/PostgreSQL
  persistence, repositories and infrastructure implementations.
- `Merconiq.Web` references Core and Infrastructure. It owns the deployable
  host, HTTP/UI, authentication/authorization pipeline and composition root.
- `Merconiq.Tests` may reference all three production projects. Production
  projects must not reference the test project.

The executable check in `scripts/validate-architecture.sh` enforces this
project-reference graph. A new project or dependency direction is an
architecture change and requires updating that check and its evidence; do not
create a service-per-capability topology to add an extension.

## Persistence, migrations and posting boundaries

The current persistence owner is `Merconiq.Infrastructure`. Its
`InventoryDbContext` contains the implemented capability tables and the EF
migrations in `src/Merconiq.Infrastructure/Migrations/`. There is one context
and one migration stream. Do not create another context, migration assembly,
or schema owner until a concrete ownership boundary and an upgrade test prove
that separation is safe. The Web host currently applies this migration stream
automatically only in Development.

For existing inventory postings, the persisted records have distinct roles:

| Record | Meaning and rule |
| --- | --- |
| `StockTransactions` | Historical physical movement evidence (including source/destination where applicable). The DbContext enforces append-only behavior. Do not edit or delete a posted movement to correct it; use the relevant authorized correction or return workflow. |
| `StockInHand` | Current on-hand quantity by item, location and lot. `ReservedQuantity` and `QuarantinedQuantity` are holds within that position, not additional physical movements. Read it as the current operational position; do not independently write it or treat it as the movement history. |
| `StockValuationEntries` | Append-only moving-average valuation postings linked to stock movements. These are inventory valuation evidence, not general-ledger journals. |
| `StockValuationBuckets` | Current weighted-average quantity/value position by item and location. Keep it consistent with the valuation entries through the existing posting service; do not use it as a second, independently writable ledger. |
| `TransferOrders`, `TransferTransitEntries` and `TransferTransitSettlements` | Transfer intent and the recorded in-transit/settlement history. A transfer extension must preserve source, transit and destination movement lineage through the existing transfer workflows. |

Posting code must use the existing application service/command and unit-of-work
boundary so validation, tenant/company authorization, audit, concurrency,
idempotency (where supported), movement/valuation consistency and transaction
rollback remain in force. Reports and adapters may read authorized application
results; they must not write another module's ledger or bypass the posting
service with direct `DbContext` writes. Never infer a general-ledger posting
from an inventory valuation entry. The current source tree has no general
ledger journal model.

## Existing compile-time seams (limited, not a plugin API)

`src/Merconiq.Web/Configuration/ApplicationServiceExtensions.cs` explicitly
registers existing Core service contracts such as `IStockService`,
`IPurchaseOrderService`, `ITransferOrderService`, `IDemandForecastService`,
`IAnomalyDetectionService` and `IWebhookDispatcher` with in-process
implementations. `ForecastingOptions.Implementation` accepts only the managed
moving-average and SSA algorithm identifiers currently supported by
`ForecastingImplementations`.

These registrations demonstrate compile-time dependency injection inside the
host. They do **not** establish a stable external extension API: registrations
are host-owned, no service interface is identified here as a supported
third-party extension contract, there is no published report/adapter
contract, and no example third-party implementation has been tested through an
application/database upgrade. Adding an alternate implementation today
requires compiling it into and registering it from the host composition root.
Any such implementation must be called only through an authorized application
entry point and preserve tenant scope, audit and posting invariants. Replacing
a registration does not itself provide those controls.

Until an adapter/report example and its upgrade fixture are implemented and
reviewed, contributors must treat these interfaces as internal implementation
seams rather than promise them as supported extension points. Do not introduce
runtime assembly discovery, arbitrary or untrusted plugin loading, a generic
plugin framework, direct cross-module ledger writes, or a second migration
owner to make an example appear extensible.

## Versioning and breaking changes

- This is a pre-release project. Internal .NET APIs and database schemas may
  change incompatibly unless a specific contract is explicitly marked stable
  in its contract documentation. Earlier pre-release APIs and schemas have no
  general backward-compatibility guarantee.
- A versioned route (for example, `/api/v1`) or a public-looking C# interface
  is not by itself a stability declaration. Each stable contract must identify
  its version, compatibility rules and owner. No support or deprecation period
  is promised unless the owner publishes one for that contract.
- Once a contract is declared stable, compatible additions stay within its
  version. A breaking change requires a new contract version and a documented
  consumer transition; do not silently change a declared stable payload or
  behavior. Webhook and import/export guarantees remain subject to the open
  integration-contract work in issue #355.
- EF Core changes use the existing forward migration history. Do not rewrite
  applied migrations, reset migration history, or silently discard approved
  data. Pre-release schema compatibility is not guaranteed, but every schema
  change still needs a reviewed migration and a tested upgrade path. Until a
  supported release baseline is declared, each upgrade test must identify its
  exact source migration state and target revision. There is currently one
  migration owner: `Merconiq.Infrastructure` / `InventoryDbContext`.
- Before describing an extension or contract as supported, add a compiled
  example and automated coverage for its behavior, authorization/audit
  boundary, and database upgrade from an explicitly identified starting
  migration state. Until those tests exist, documentation of a seam is not
  evidence that an external extension works.

## Remaining evidence for a supported extension contract

This page records the current ownership, dependency, persistence and versioning
boundaries. It does not complete the full extension acceptance scope. The
repository still needs a minimal report or adapter example that can be compiled
without edits to core business logic, proves that it cannot bypass
authorization/audit or write another module's ledger, and is exercised by an
upgrade fixture. The integration contracts and evidence tracked by issue #355
also remain open; they do not prevent this current-state documentation, but
they must be resolved before claiming the broader M18 integration/extension
contract is complete. The M10 qualification prerequisite is also independent
and is not satisfied by this documentation. Keep issue #359 open until the
remaining evidence and prerequisites are reviewed.
