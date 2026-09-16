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
stock in this slice. FIFO, lot/expiry valuation, transfers, opening-baseline
backfill, reservations, and GL postings remain separate follow-up work.

A quantity-only sale with no valuation bucket remains unvalued and creates no
valuation entry. If a bucket exists, a sale must be fully covered by its valued
quantity; a sale that would combine valued and unvalued quantities is rejected
atomically. Partial valuation of one sale is not inferred in this slice.
