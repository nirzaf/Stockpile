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
| Sales / customer master | Customer entity in `src/Merconiq.Core`; tenant/company ownership, migration and audit in `src/Merconiq.Infrastructure`; company-capability API in `src/Merconiq.Web` | Implements independent company-owned customer records and configuration-only payment-term days. Quotations, orders, invoices, receivables, credit controls, shared customer masters and UI/import workflows remain future work. |
| Integrations and decision support | Contracts and forecasting services in `src/Merconiq.Core`; webhook delivery, configuration, host services and persistence registrations in `src/Merconiq.Infrastructure` and `src/Merconiq.Web` | The HTTP and webhook behavior documented in `docs/API.md` is part of the declared public v1 contract. Issue #355 tracks additional implementation and evidence; future, undocumented integration behavior is not thereby promised as a stable contract. |
| General ledger / finance | A company-owned, configuration-only chart-of-account catalog stores company-supplied codes, names, free-form type labels, hierarchy and active/group flags through an API | It is not a journal or posting subsystem. No account defaults/taxonomy, control-account mapping, fiscal period, base-currency posting, tax policy or finance approval is supplied; stock valuation records are not journal entries and are not an accounts-payable or financial-statement source of truth. |

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
| `StockTransactions` | Append-only stock-operation evidence. Physical movement types record quantity movement; `Quarantine` and `QuarantineRelease` record hold changes without changing on-hand quantity. Interpret `TransactionType` before reconciling quantities; do not count every row as physical flow. The DbContext enforces append-only behavior. |
| `StockInHand` | Current on-hand quantity by item, location and lot. `ReservedQuantity` and `QuarantinedQuantity` are holds within that position, not additional physical movements. Read it as the current operational position; do not independently write it or treat it as the movement history. |
| `StockValuationEntries` | Append-only moving-average valuation postings linked to valued stock movements. These are inventory valuation evidence, not general-ledger journals. |
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

`tests/Merconiq.Tests/Integration/ExtensionUpgradeFixturePostgreSqlTests.cs`
defines one bounded, test-only example around the existing MediatR
`CreateItemCommand` pipeline. It starts at the explicitly named
`20260918183139_EnforceAuditLogAppendOnly` migration, applies the current
forward migrations, then sends an authenticated item-creation request through
the Web host with the adapter registered by dependency injection. Its assertions
cover the host authorization boundary, the existing EF audit record, and the
absence of writes to stock, valuation, procurement, and transfer ledgers. This
is an executable example of an in-process observer around one existing
operation; it does not publish a third-party report, rules, or adapter API and
does not establish that arbitrary business behavior can be replaced safely.

The interfaces remain internal implementation seams until a stable external
contract is explicitly designed, reviewed, versioned, and qualified. Do not
introduce runtime assembly discovery, arbitrary or untrusted plugin loading, a
generic plugin framework, direct cross-module ledger writes, or a second
migration owner to make an example appear extensible.

## Versioning and breaking changes

- `CHANGELOG.md` records a `1.0.0` release and states that the project follows
  Semantic Versioning. `docs/API.md` explicitly identifies `/api/v1` as the
  public HTTP API contract. Accordingly, treat the documented `/api/v1`
  request, response, authentication and behavior contract as stable: compatible
  additions may remain in v1, but a breaking HTTP contract change requires a
  new API version and a documented consumer transition. Do not silently change
  documented v1 behavior.
- The public HTTP API declaration does not by itself make every C# type, DI
  registration, or EF table a public extension contract. The service
  registrations described above remain host-owned implementation seams. A
  .NET or database contract becomes stable only when its scope and compatibility
  rules are explicitly documented.
- Existing import/export routes and webhook behavior documented in
  `docs/API.md` are part of the declared public v1 contract and follow the same
  compatibility rule, including documented authentication, response, event
  identity, signature, retry and payload guarantees. Issue #355 tracks
  additional implementation and acceptance evidence; its open status is not an
  exception to those existing v1 guarantees. Undocumented future behavior is
  not promised as part of the stable contract.
- Once another contract is declared stable, compatible additions stay within
  its declared version. A breaking change requires a new contract version and
  a documented consumer transition. No support or deprecation period is
  promised unless the owner publishes one for that contract.
- EF Core changes use the existing forward migration history. Do not rewrite
  applied migrations, reset migration history, or silently discard approved
  data. The repository does not declare the EF schema as a public contract or
  promise downgrade compatibility; every schema change still needs a reviewed
  migration and a tested, data-preserving forward upgrade. Each upgrade test
  must identify its exact source migration state and target revision. There is
  currently one migration owner: `Merconiq.Infrastructure` /
  `InventoryDbContext`.
- The PostgreSQL fixture above is scoped to one compiled command observer and
  an explicitly identified forward migration. It does not make a C# service,
  MediatR handler, DI registration, or EF table a supported third-party
  contract. A future external contract needs a separately reviewed definition,
  versioning rules, and automated coverage for its behavior, authorization,
  audit, and upgrade boundaries; documentation of an internal seam alone is
  not evidence that an external extension works.

## Remaining evidence for a supported extension contract

This page records the current ownership, dependency, persistence and versioning
boundaries. The PostgreSQL fixture is a focused example, not completion of the
full extension acceptance scope: it covers a test-only observer, not an
external report or rules API, and still requires successful PostgreSQL CI and
review. The integration contracts and evidence tracked by issue #355 also
remain open; they do not prevent this current-state documentation, but they
must be resolved before claiming the broader M18 integration/extension
contract is complete. The M10 qualification prerequisite is also independent
and is not satisfied by this fixture. Keep issue #359 open until the remaining
evidence and prerequisites are reviewed.
