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
| Currency metadata | Company base-currency field | A company requires an explicitly supplied three-letter currency code and 0–4 decimal-place currency scale; there is no jurisdiction-specific default. Currency and scale may change during setup but are frozen after posted stock activity. Validation against a maintained ISO 4217 catalog remains future M01 work; do not assume every currency has two fractional digits. |

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

## Currency and item quantity conventions

Company `CurrencyScale` is the explicit number of fractional decimal places used
for money display and rounding. New company writes must supply it from the
approved business configuration; values from 0 through 4 are supported. Legacy
rows may remain unconfigured until an owner supplies the value. Neither the
country field nor the currency code selects a scale, and the application does
not claim a maintained ISO 4217 catalog.
Updates that omit `CurrencyScale` preserve the current value for compatibility;
an explicit value is required when configuring a new company or initializing a
legacy company.

Items keep `Rate` as the current selling price. Acquisition cost is supplied by
costed stock postings and moving-average valuation; it is never inferred from
`Rate`. A base unit may have separate purchasing and sales units with positive
conversion factors. The server validates those references in the current tenant,
rejects deleted units, rejects inconsistent factors and precision, and rejects
fractional quantities for whole-unit-only items. Barcodes are trimmed, limited to
100 characters and unique within a tenant.
The item update API preserves an existing barcode when the field is omitted;
send `clearBarcode: true` to remove it explicitly.

The existing receive, transfer, sale and reservation contracts remain positive
integer quantities. The tested carton-to-base conversion helper is available for
future adapters, but no decimal stock API is implied here. Weighted goods and
other fractional stock require an explicitly versioned additive quantity contract
before they can be posted; callers must not silently round them.

An owner-approved mapping may assign a branch to an existing unmapped location
without moving or rewriting its stock history. Once a location already has a
branch and posted stock activity, its branch ownership cannot be changed;
historical movements remain tied to the company context under which they were
posted. Branch assignment and stock posting share tenant-scoped, per-location
transaction locks, so the history check cannot race a movement commit.

User-facing stock commands carry the company scope that was authorized before
dispatch. After acquiring the same location lock, the stock service verifies
that each affected location still belongs to that company and rechecks the
company capability; a reassignment or revocation that wins before posting causes
the old authorization to fail closed. Reservation
creation, release, cancellation, consumption, and opening-stock reversals use
the location lock as well. A reversal serializes by import before checking its
correction record, then acquires all affected location locks in ascending order
before posting, so concurrent retries are idempotent and cannot deadlock against
multi-location transfers.

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
