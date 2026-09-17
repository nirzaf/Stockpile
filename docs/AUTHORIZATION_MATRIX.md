# Tenant and company authorization

## Enforcement model

Identity roles are capability ceilings, not company grants. Every named capability policy revalidates the current tenant, user and security stamp, then requires an active membership granting that capability in at least one company. Resource endpoints and actions must additionally resolve the resource's company from persisted ownership and check the requested capability there. A company ID selected by the browser is never proof of access.

Tenant `Admin` is the explicit tenant-wide administrator exception. `CompanyAdmin` is not global: it needs an active `Administer` grant for the target company. Tenant-scoped, companyless operations are restricted to tenant `Admin` until their ownership contract is implemented.

| Role | View | Edit | Approve | Post | Reverse | Administer |
| --- | --- | --- | --- | --- | --- | --- |
| Operator / Staff | yes | no | no | yes | no | no |
| Buyer / Manager | yes | yes | no | yes | no | no |
| Accountant | yes | no | yes | yes | yes | no |
| Cashier | yes | no | no | yes | no | no |
| CompanyAdmin | yes | yes | no | no | no | yes |
| RestrictedAuditor | yes | no | no | no | no | no |
| Tenant Admin | tenant-wide | tenant-wide | tenant-wide | tenant-wide | tenant-wide | tenant-wide |

Effective access is the intersection of the role row and the active membership's capability flags. Granting a capability does not override the role ceiling. `View` is added automatically to every non-empty membership grant. Revoking or changing a membership updates the user's Identity security stamp; JWTs are checked against the current stamp and active Blazor actions re-check authorization.

Segregation-of-duties rules (for example, prohibiting the same person from preparing and approving a document) are not enabled by this matrix; they require an explicit business-owner decision and separate workflow evidence.

## Current resource boundaries

| Resource or operation | Current scope and rule | Remaining qualification |
| --- | --- | --- |
| Companies and branches | Tenant-filtered. Reads, edits and branch creation resolve the target company and require the corresponding grant. Company creation is tenant-Admin only. | Exercise with PostgreSQL constraints and two tenants. |
| Locations and stock | Location ownership resolves through Branch → Company. Stock reads and movements are filtered/checked against those persisted owners. Unassigned legacy locations are tenant-Admin-only; transfers require two mapped locations in the same company. | Review legacy mapping before enabling ordinary company operations. |
| Items and suppliers | Shared tenant masters (no `CompanyId` in the current schema). Require a company membership; reads use `View`, changes use `Edit`. Their tenant-keyed item cache contains shared-master data, not company-specific stock history. | Confirm with the business owner whether these masters remain shared or become company-owned. |
| Purchase orders | Existing records have no company/branch/location owner, so they remain tenant-scoped and are exposed only to tenant `Admin`. Do not infer ownership from the supplier or current UI selection. | MER-M05 must add an explicit company/receiving-location ownership contract before widening access. |
| Opening-stock preview, replay and reversal | CSV validation and approved baseline replay are restricted to tenant `Admin` plus the `Approve` capability; reversal additionally requires `Reverse`. Replay requires server-derived approver metadata, an explicit cutover, stable references, current-tenant masters, reconciled quantities and an atomic append-only lineage record. Reversal creates forward stock effects and retains the original baseline. | M06 must add the separately approved opening-balance journal and financial activation gate. |
| Master-data imports and webhook subscriptions | Company creation remains tenant-wide and restricted to tenant `Admin`. Branch and location imports require the target company's `Edit` or `Administer` capability; shared item, supplier and UOM imports require an explicit active `CompanyId` scope plus their normal capability ceiling. Shared masters remain tenant-owned; the company ID is an authorization context, not inferred ownership. | A future company-owned master requires an explicit schema and delivery contract. |
| Forecasts and anomalies | Authenticated company members receive results filtered to their authorized company IDs. Forecast result caches include the sorted company scope; tenant `Admin` and the maintenance runner use a separate explicit tenant-wide scope. | Verify output isolation with PostgreSQL-backed fixtures and refresh/cache behavior. |
| Background forecast maintenance | A privileged system task enumerates configured tenant IDs and creates a fresh scope for each tenant. It does not impersonate a user. Its tenant-wide cache key is distinct from every company-scoped request key. | Any future company-owned background work must enumerate an explicit company scope and use company-specific keys. |
| User and role administration | Tenant `Admin` only. Company memberships are managed through target-company `Administer` checks. | A dedicated membership-management UI is not yet provided; API/administrative tooling is currently required. |

All API reads, writes, imports and exports must retain the same resource check as the screen. A hidden button is not authorization. This repo does not currently expose attachment/document download endpoints; add resource checks before introducing them. System tasks may use tenant-wide privilege only where the operation is intentionally tenant-scoped and documented above.

## Verification status

Automated tests cover the six capability personas, company/stock filtering, unmapped transfer rejection, membership revocation, shared-item API gating, forecast cache separation and company-filtered anomaly predicates. The current local test environment does not provide PostgreSQL; database constraints, transaction behavior and migration execution remain unqualified here. Do not treat this matrix or passing in-memory tests as production authorization certification.
