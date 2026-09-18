# Inventory valuation

Merconiq stores moving-average cost at the tenant/company/item/location scope in
base stock units. A valuation bucket has no batch or expiry dimension: explicit-
cost receipts for tracked lots update the same item/location bucket, and the
selling rate is never used as acquisition cost. Existing quantity-only stock
movements remain supported and remain unvalued.

Each valued receive or sale creates one append-only valuation entry linked to its
`StockTransaction`. A valuation bucket keeps the current quantity and total value:

- a receipt adds `quantity * UnitCost`;
- a sale consumes the current weighted average cost;
- a sale of the final valued units consumes the exact remaining bucket value.

Valuation updates commit atomically with the stock movement and use PostgreSQL
`xmin` optimistic retries. Lot and expiry identify quantity for traceability only;
they do not establish separate cost pools or FIFO layers. Lot-tagged valued entries
retain the source movement's lot identity so an unvalued lot cannot borrow another
lot's valuation coverage. An approved opening baseline uses an explicit cutover instant,
creates an `Opening` stock transaction and valued receipt entry for each source
line, and links the import line to that movement. The dry-run reports current
versus approved quantities; replay rejects an existing quantity mismatch and
rejects a scope that is already valued. A tenant can have one baseline, and the
import, lines and correction record are append-only at both EF and PostgreSQL
boundaries.

Missing historical cost is rejected; an explicit zero cost is different from a
missing cost. Financial activation and its separately approved M06 opening journal
are not enabled by this stock slice. A baseline correction uses the approved
reversal endpoint, which posts forward stock/valuation effects and retains the
original baseline. Backups and restore rehearsals remain operational prerequisites;
rollback of a failed request is the surrounding database transaction, not a
production database reset. FIFO and GL postings remain separate follow-up work;
transfers, reservations, and expiry eligibility are controlled by the stock
service rather than this valuation slice.

A quantity-only issue with no valuation bucket remains unvalued and creates no
valuation entry. Tracked-lot issues use the shared item/location moving average
only when immutable valuation movements cover the selected lot's full on-hand
quantity and the aggregate bucket covers the issue. Unvalued or mixed valued and
unvalued quantities in the selected lot cannot consume value belonging to another
lot. Such mixed lot positions are rejected atomically; partial valuation of one
lot issue is not inferred.

## Lot expiry restrictions

Stock operations that target an expired lot reject it by default before
reserving (including FEFO selection), selling (including consuming a
reservation), or transferring its quantity out. Expiry is date-only and
compared with the current UTC calendar date; a lot remains eligible through
its recorded expiry date. A rejection does not change on-hand quantity,
reservations, or stock movements.

An exception requires both a nonblank `ExpiryExceptionReason` (up to 500
characters) and the company-scoped `OverrideExpiredStock` capability, in
addition to the normal stock-post permission. The existing `Admin` and
`Accountant` roles may hold this capability, but both must have an active,
explicit grant for the company that owns the location. The tenant-wide Admin
bypass used by other capabilities does not apply to this override, and locations
without a branch/company owner cannot use it. The API checks the grant before
dispatch and the stock service rechecks it after acquiring the location lock.
Direct service callers that do not provide the authorization callback cannot
override expiry. Supplying a reason for a non-expired lot is rejected.

Accepted reasons are persisted separately on the stock reservation, its
lot-specific allocation, or the stock movement and included in the normal audit
log. The same rule applies to
reservation creation, reservation consumption, sales, and transfers. An
un-pinned reservation uses FEFO and can split its quantity across as many
eligible, unexpired lots as needed by default. Expired lots participate only
when the request explicitly supplies an expiry-exception reason and the caller
has the required company-scoped override capability. Its allocation view preserves that lot order and each
allocation's quantity, consumption, and expiry-exception reason. A request
pinned to a batch or expiry date remains limited to that lot, and a reservation
that cannot be fully allocated fails rather than reserving only part of the
requested quantity. This does not automatically quarantine expired stock.

## Quarantined stock

Quarantined quantity remains part of on-hand stock but is excluded from
available-to-promise quantity and cannot be reserved, sold, or transferred out.
Quarantining moves only currently available units into the quarantine balance;
it does not change physical quantity or valuation. Each quarantine and release
requires a source-line reference and a reason recorded with the stock movement.
When a selected lot has an expiry date, the request must include that date so
the movement is tied to one exact lot. Replaying the same source line is
idempotent only when its request matches.

Releasing quarantined units requires the normal company-scoped stock-post grant
and an additional explicit `OverrideQuarantinedStock` company grant. This
additional capability is never implied by the tenant Admin role; only Admin and
Accountant roles may hold it, and a location without an owning company cannot
use it. The service rechecks the posting and override grants after acquiring the
location lock. Release restores availability without changing on-hand quantity
or valuation. FEFO reservations can allocate across multiple lots; a complete
quarantine-review workflow remains follow-up work.

The current PostgreSQL schema stores expiry in `timestamp with time zone`
columns. At service lookup and EF persistence boundaries, Merconiq preserves the
supplied year/month/day and normalizes the value to UTC midnight. An unspecified
`DateTime` or a value with a time component therefore cannot shift the expiry
calendar date or create a second identity for the same lot. Date-based lookup
also recognizes an existing row with a non-midnight timestamp on that UTC day;
the row is canonicalized when it is next modified. If multiple stock rows match
the same item, location, batch and expiry date, mutation fails with a conflict
so the balances can be reconciled explicitly instead of silently choosing one.
Availability groups matching stock and reservation timestamps by calendar date.
An idempotent retry returns its existing active reservation before re-running
first-expiry selection; reservation consumption still enforces expiry.

## Transfer-order dispatch

Dispatch is supported for approved transfer reservations only when the source
warehouse has an existing moving-average valuation bucket covering the dispatched
quantity. For tracked stock, immutable valuation movements must also cover the
selected source lot's full on-hand quantity; an unvalued lot cannot consume another
lot's value merely because both share an item/location bucket. The source bucket's
weighted-average carrying value is captured at dispatch, reduced atomically with
the source quantity/reservation, and recorded with the exact batch/expiry and
transfer-order line identity in the company-scoped transit ledger. The bucket is
not split or revalued per lot, and transit value is not recalculated from selling
price. Dispatch creates no destination quantity; a later receipt or return posts
its own source-linked transit movement.

Quantity-only and mixed valued/unvalued lots remain ineligible for valued dispatch.
Cross-company transfers remain unsupported. Dispatch entries are append-only,
and the transfer cannot be amended or cancelled after dispatch. The dispatch
notification is written to the durable outbox in the same database transaction;
delivery happens asynchronously after commit and cannot undo a committed movement.

## Transit settlement

A receipt or quarantine creates a valued `TransferIn` entry at the destination
from the dispatch-captured transit value. A source-linked return creates a
valued `TransferReturn` entry at the source from that same captured value. An
approver write-off requires the existing company-scoped `Approve` capability
and a mandatory reason. It is a source-linked transit settlement that records
the apportioned dispatch-captured value without a physical stock movement or
stock transaction; it does not post a GL journal, which remains in M06.
Partial settlements allocate value from the captured total; the last settlement
takes the exact remaining value so rounding cannot create or destroy transit
value. Settlement rows are append-only and reference the transit entry,
transfer-order line, and source document line; physical receive, quarantine,
and return events also reference their stock transaction. A write-off has no
stock transaction by design. Partial settlement leaves the unallocated balance
in transit. Transit settlement retains the source batch and expiry identity
while carrying the dispatch-captured item/location moving average;
it does not create lot-specific costing. Quantity-only dispatch remains unvalued
and is rejected at this valued-transit boundary.
