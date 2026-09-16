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
production database reset. FIFO, lot/expiry valuation, transfers, reservations,
and GL postings remain separate follow-up work.

A quantity-only sale with no valuation bucket remains unvalued and creates no
valuation entry. If a bucket exists, a sale must be fully covered by its valued
quantity; a sale that would combine valued and unvalued quantities is rejected
atomically. Partial valuation of one sale is not inferred in this slice.
