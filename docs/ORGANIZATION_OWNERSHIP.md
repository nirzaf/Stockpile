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
| Currency metadata | Company base-currency field | A company requires an explicitly supplied three-letter currency code; there is no jurisdiction-specific default. It may change during setup but is frozen after posted stock activity. Currency-specific display and rounding metadata, and validation against a maintained ISO 4217 catalog, remain future M01 work; do not assume every currency has two fractional digits. |

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

## Document identity and legacy purchase orders

The document-identity migration assigns every existing purchase order a new,
stable internal UUID and preserves its existing `PONumber` verbatim as the
human-facing document number. Existing order-detail rows receive stable line
UUIDs linked to that purchase-order identity. The migration does not renumber,
delete, or infer ownership for any existing document.

Legacy purchase orders and their lines remain `CompanyId = NULL` because the
current purchase-order model has no company or branch owner. Their identity
mapping is tenant-scoped, and line-link creation fails closed until both lines
have an owner-approved company mapping. The supplier, current user, or selected
UI context is not evidence of legal-company ownership. A later ownership
change must supply and validate that mapping explicitly.

Purchase-order create retries use a tenant-scoped idempotency key and a hash of
the business request; replaying the same key and request returns the retained
purchase order, while reusing it for different content is rejected. Cancellation
and voiding change lifecycle state but retain the document identity, human
number, and line records. The current implementation covers purchase orders;
it does not claim document identity or lineage support for receipts, invoices,
payments, or other business documents that are not yet modeled.
