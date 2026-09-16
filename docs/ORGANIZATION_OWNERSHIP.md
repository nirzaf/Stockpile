# Organization ownership and migration policy

Company and branch ownership is additive to the existing tenant boundary.
`TenantId` continues to identify the security boundary; it is never treated as
a company identifier.

## Master ownership

| Master | Ownership in M01 | Current transition |
| --- | --- | --- |
| Company | Tenant-owned | New `Companies` rows require a tenant-scoped unique code. |
| Branch | Company-owned within a tenant | New `Branches` rows require a same-tenant company and a company-scoped unique code. |
| Location | Branch-owned when mapped | Existing locations keep their IDs and receive nullable `BranchId` until an owner-approved mapping exists. |
| Item, supplier, purchase order, stock | Existing tenant-scoped masters | No automatic legal-company inference is performed in this migration; mapping is a controlled follow-up. |
| Currency metadata | Company base-currency field | The current foundation stores the ISO-style base-currency code; a shared catalog is a later M01 issue. |

The migration creates `Companies` and `Branches`, then adds nullable
`Locations.BranchId`. Composite foreign keys include `TenantId`, so a branch
or location cannot reference a company/branch from another tenant at the
database boundary. The service also rejects inactive companies/branches and
forged ownership combinations before saving.

Existing stock rows are not copied, re-keyed, or recalculated. Because location
IDs and the existing stock relationships remain unchanged, current quantities,
batches, expiry data, and movement history remain addressable. A maintainer or
business owner must supply the mapping for legacy locations and other masters;
the application does not invent a legal name or infer a company from a tenant
ID.

The API surface is under `/api/v1/organization`: company and branch list,
search, create, update/deactivate, plus controlled location-to-branch
assignment. Mutations require the existing Admin or Manager role.

Stock receive, sell, and transfer operations resolve every location through
the current tenant scope. Transfers between two branch-owned locations are
limited to one company; intentionally unmapped legacy locations remain usable
until an owner-approved mapping is supplied. Stock/location foreign keys also
carry `TenantId`, preserving existing integer IDs while making a tenant-mixed
reference invalid at the database boundary.
