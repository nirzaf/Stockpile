# Stranded transfer transit runbook

Use this procedure when a dispatched transfer still has outstanding transit
quantity or when its quantity/value reconciliation needs investigation. It uses
only the transfer-order API and UI behavior currently available in Merconiq. It
does not authorize changing records to force a balance.

## Safety rules

- Use an authenticated API identity with current `View` access to the company
  for investigation and `Post` access to both transfer locations for a
  settlement. The API checks company/location authorization when each request
  runs; a previous grant or another operator's access is not sufficient.
- Confirm the legal company and both locations from the transfer record and
  current authorized company records. Location IDs are not a historical branch
  responsibility mapping. Do not infer which branch/company owned a historical
  transfer from its location name or from another issue's proposed mapping.
- **Never use raw SQL, direct database edits, or migration/history edits to
  settle, repair, or rebalance transit.** Use the supported API/UI so the
  settlement, stock transaction, valuation entry, and source-document lineage
  are written together in the application transaction. If the supported flow
  cannot represent the facts, stop and escalate.
- Age, a nonzero variance, or the last recorded actor is evidence to investigate,
  not permission to post a correction. The aging report is read-only and does
  not automatically repair anything.
- Choose receive, quarantine, or return only when physical and documentary
  evidence supports that exact disposition. Do not treat quarantine as a
  write-off or final disposition.

## 1. Locate and review the transfer

Call the read-only aging endpoint with an API JWT for the correct tenant. The
caller must have company `View` capability. Add `companyId` only when its current
company grant has been confirmed. The page is bounded to 100 lines, defaults to
50, and uses `nextAfterLineId` as the exclusive cursor for the next page.

```http
GET /api/v1/transfer-orders/aging?pageSize=50&companyId={companyId}
Authorization: Bearer {jwt}
```

Follow `nextAfterLineId` with `afterLineId` until the relevant line is found.
Record the report `asOf` time, transfer order/document IDs, source line ID, item,
company, source and destination location IDs, order status, and the quantities
and values reported. Review at least:

- `outstandingTransitQuantity`, `outstandingTransitValue`, and oldest outstanding
  age;
- `dispatchedQuantity`, received, quarantined, returned, and the quantity/value
  conservation variances;
- dispatch and settlement ledger/valuation variances; and
- latest transit action, actor, and timestamp, noting that this is not a complete
  approval or action history.

Zero conservation and ledger variances mean the persisted records agree for the
report's checks; they do not establish where the goods physically are. A nonzero
variance, missing or contradictory source evidence, or unclear company/location
ownership is a stop condition: preserve the response and escalate to the
inventory owner before posting any settlement.

Read the transfer order for its numbered document, company, locations, source
line, and dispatch entries. The order-detail route requires `View` permission for
the company and locations:

```http
GET /api/v1/transfer-orders/{transferOrderId}
Authorization: Bearer {jwt}
```

The transfer-orders page is also available to authorized users at **Stock →
Transfer Orders**. It shows each dispatch entry's ID, remaining quantity/value,
batch/expiry where present, and existing received/quarantined/returned totals.

## 2. Assemble evidence before acting

Retain a dated case record containing the aging response and order-detail
response (or equivalent UI evidence), including:

- tenant and confirmed company; transfer number, order/document ID, source line
  ID, item, source/destination location IDs, and transit-entry ID;
- dispatched quantity/value, remaining quantity/value, batch/expiry when present,
  and the existing settlement totals;
- the source and destination documents, receiving/shipping records, physical
  count or inspection, and operator confirmation that support the proposed
  disposition;
- relevant stock transaction and valuation-ledger evidence, plus any nonzero
  variance fields, as-of time, and relevant actors/timestamps; and
- the chosen action, quantity, reason/notes, idempotency key, request result, and
  post-action aging/order responses.

Keep evidence within the authorized tenant/company. The aging response is a
summary rather than a full event-history export; record what the available
authorized UI/API actually returns and do not claim it proves unreported history
or branch responsibility.

Use the current read-only stock endpoints to collect corroborating ledger
evidence. Both require company `View` capability and scope non-administrator
results to companies the caller may view:

```http
GET /api/v1/stock/transactions?from={utc-start}&to={utc-end}
Authorization: Bearer {jwt}

GET /api/v1/stock/valuation?itemId={itemId}&locationId={locationId}
Authorization: Bearer {jwt}
```

The transaction endpoint accepts an optional date window. Compare only records
that match the transfer's item, locations, quantity, and time/source references;
the endpoint is not a transfer-specific history feed. The valuation endpoint
shows current moving-average buckets and immutable valuation entries. Preserve
the responses with the same case record and note the query window and filters.

## 3. Select only an evidence-supported action

The API settlement routes require `Post` access to both locations in the same
company, an authenticated operator identity, a positive quantity no greater
than the transit entry's current remaining quantity, and an
`Idempotency-Key` of at most 200 characters. Each action is append-only and
limited to the unsettled dispatch quantity.

- **Receive** only the quantity physically confirmed at the destination and
  supported by receiving evidence. It increases destination on-hand stock.
- **Quarantine** only the quantity physically at the destination but held due to
  a documented discrepancy or safety/quality concern. Supply a nonblank reason.
  Quarantined stock remains valued stock and unavailable for normal stock
  operations; quarantine is not a write-off.
- **Return** only the quantity confirmed returned to the source location. It
  restores the captured transit value to source stock.

Do not use an action merely to clear an aging balance. If stock is missing, its
location is disputed, its company cannot be confirmed, the value/ledger evidence
does not reconcile, or the facts need a disposition not offered above, leave the
transit open and escalate to the inventory owner. Do not invent a write-off,
branch assignment, cost, or accounting treatment.

For example, receive 20 units from transit entry 789 on line 456 of order 123:

```http
POST /api/v1/transfer-orders/123/lines/456/transit/789/receive
Authorization: Bearer {jwt}
Idempotency-Key: {unique-key-retained-for-this-attempt}
Content-Type: application/json

{"quantity":20,"notes":"Received and counted at destination; case {reference}"}
```

Use the same endpoint with `/quarantine` (and a nonblank `reason`) or `/return`
only for those evidenced outcomes. Preserve the response and then re-read the
order and aging report to verify the resulting quantity/value balances and
ledger variances. Do not assume a successful HTTP response alone proves the
physical count.

## 4. Handle an uncertain response safely

If the request times out or its response is lost, do not create a new key or
change the action/quantity. Retry the exact same method, URL, idempotency key,
and request body. The key is scoped to the request route and body; reusing it
with different content is rejected. Keep the key with the case evidence. Once
the original outcome is recovered, refresh the order and aging report before
making any further decision.

If the retry cannot recover a clear outcome, or the current remaining quantity
has changed, stop and ask the inventory owner to review the captured evidence.
Do not issue a compensating action until the persisted state and physical facts
are understood.

## Current limits

The aging report reports persisted location IDs but intentionally does not
assign historical branch responsibility. Do not fill that gap by guessing or
by applying an unverified historical mapping. The report also provides no
write-off action; quarantine is not a substitute. Lot/expiry must match the
dispatched entry, and unsupported valuation/lot cases require owner review.
Consult the [API reference](API.md#transfer-order-dispatch) for exact endpoint
contracts and report-field definitions.
