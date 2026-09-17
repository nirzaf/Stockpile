# Inventory valuation

Merconiq stores moving-average cost for unbatched stock at the tenant, item, and
location scope. A costed receipt must provide an explicit `UnitCost`; the item
selling rate is never used as acquisition cost. Existing quantity-only stock
movements remain supported and remain unvalued.

Each valued receive or sale creates one append-only valuation entry linked to its
`StockTransaction`. A valuation bucket keeps the current quantity and total value:

- a receipt adds `quantity * UnitCost`;
- a sale consumes the current weighted average cost;
- a sale of the final valued units consumes the exact remaining bucket value.

Valuation updates commit atomically with the stock movement and use PostgreSQL
`xmin` optimistic retries. Costed postings are intentionally limited to unbatched
stock in this slice. An approved opening baseline uses an explicit cutover instant,
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
production database reset. FIFO, lot/expiry valuation and GL postings remain
separate follow-up work; transfers, reservations, and expiry eligibility are
controlled by the stock service rather than this valuation slice.

A quantity-only sale with no valuation bucket remains unvalued and creates no
valuation entry. If a bucket exists, a sale must be fully covered by its valued
quantity; a sale that would combine valued and unvalued quantities is rejected
atomically. Partial valuation of one sale is not inferred in this slice.

## Lot expiry restrictions

Stock operations that target an expired lot reject it before reserving, selling
(including consuming a reservation), or transferring its quantity out. Expiry
is date-only and compared with the current UTC calendar date; a lot remains
eligible through its recorded expiry date. Rejection does not change on-hand
quantity, reservations, or stock movements. Expired stock is not automatically
quarantined, and audited exceptions or overrides are not supported.

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
