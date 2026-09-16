# Merconiq modular-monolith boundaries

Merconiq is one deployable ASP.NET Core process backed by PostgreSQL. The
repository uses a small number of projects and keeps business capabilities
inside those projects rather than introducing a service-per-module topology.

## Ownership

| Area | Owner | Responsibilities |
| --- | --- | --- |
| Platform | `src/Merconiq.Core` and `src/Merconiq.Web` | Identity, tenant context, auditing, idempotency contracts, shared validation and integration boundaries |
| Inventory | `src/Merconiq.Core`, `src/Merconiq.Infrastructure`, `src/Merconiq.Web` | Items, locations, stock balances, stock movements, forecasting and anomaly detection |
| Procurement | `src/Merconiq.Core`, `src/Merconiq.Infrastructure`, `src/Merconiq.Web` | Suppliers, purchase orders and procurement workflows |
| Finance | Reserved extension area in the existing projects | Future document controls, valuation and accounting capabilities; no finance module is claimed until its issue is implemented |
| Sales | Reserved extension area in the existing projects | Future customer and sales capabilities; no sales module is claimed until its issue is implemented |

The Core project owns domain entities, commands, queries, validators, service
contracts and domain services. Infrastructure owns persistence and external
delivery implementations. Web owns HTTP/UI composition, authentication,
authorization and presentation. Tests may reference all production projects;
production projects must not reference tests.

## Allowed dependency direction

```text
Merconiq.Core <- Merconiq.Infrastructure <- Merconiq.Web
       ^                ^                  ^
       +------------ Merconiq.Tests -------+
```

Core has no project reference. Infrastructure references Core only. Web may
reference Core and Infrastructure. Tests are the only project allowed to
reference every production project. These rules keep one deployable process,
preserve the existing CQRS and PostgreSQL behavior, and prevent circular
dependencies without adding an application/shared-kernel assembly.

The executable check in `scripts/validate-architecture.sh` verifies this
project graph in CI. New capabilities should identify their owning area in
the issue and remain within these project boundaries unless an approved
architecture change says otherwise.
