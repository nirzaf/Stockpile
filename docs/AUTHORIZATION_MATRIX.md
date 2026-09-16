# Server authorization capability matrix

All API endpoints first require the existing `Api` policy, which validates an authenticated JWT and its tenant claim. Capability policies then enforce the operation on the server; a selected company in the client is never an authorization decision.

| Persona | View | Edit masters | Approve | Post | Reverse | Administer |
| --- | --- | --- | --- | --- | --- | --- |
| Operator / Staff | yes | no | no | yes | no | no |
| Buyer / Manager | yes | yes | no | yes | no | no |
| Accountant / Manager | yes | yes | no | yes | no | no |
| Cashier / Staff | yes | no | no | yes | no | no |
| Company administrator / Admin | yes | yes | yes | yes | yes | yes |
| Restricted auditor | yes | no | no | no | no | no |

The current role-to-capability mapping is intentionally conservative and additive. Company membership and per-company capability grants remain the next authorization layer; endpoints must not infer company access from a tenant ID.

Current API capability assignments include `View` for stock reads and webhook
listing, `Post` for stock receive/transfer/sell, and `Edit` for webhook
create/update/delete. These named policies preserve the existing role mapping
while keeping endpoint authorization centralized for later company-membership
checks.
