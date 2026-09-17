# Controlled master-data onboarding

The organization API supports dry-run-first, row-validated onboarding for companies,
branches, locations, suppliers, units, and items. Requests use the existing bearer-token
API and return a summary with `created`, `unchanged`, `rejected`, and row-level results.

`DryRun` defaults to `true`. An apply request runs in one retryable tenant transaction and
uses the stable external ID (plus the relevant code where one exists) for replay safety.
If any row is rejected, no new row is saved. Normal entity audit logging records successful
inserts. The import does not create opening financial balances or infer accounting ownership.

Company, branch, and location rows have persisted external IDs. Items, suppliers, and units
remain tenant-shared in the current ownership model. Their required `CompanyId` is the
operator's active company permission and onboarding scope; it must not be treated as a
company ownership column.

During upgrade, a legacy unit with no source-system external ID receives a reserved
placeholder beginning with `__merconiq_legacy_unmapped_unit__:`. This value records
that the source identity is unknown; it is not a source-system ID and the importer
rejects that reserved prefix. Do not infer a mapping from a unit code or name. Keep
such units unchanged until an owner-approved mapping workflow is completed; that
broader mapping remains tracked by issue #271.

## Endpoints and CSV headers

All paths are under `/api/v1/organization`.

| Endpoint | Required scope | Header |
| --- | --- | --- |
| `POST companies/import` | Tenant `Admin` | `external_id,code,legal_name,trading_name,registration_number,tax_identifier,base_currency,country_code,currency_scale,is_active` |
| `POST branches/import` | `CompanyId` + `Edit` | `external_id,code,name,address,time_zone_id,is_active` |
| `POST locations/import` | `CompanyId` + `Administer` | `external_id,branch_external_id,name,address` |
| `POST suppliers/import` | `CompanyId` + `Edit` | `external_id,name,contact_person,phone,email,address` |
| `POST units/import` | `CompanyId` + tenant `Admin` | `external_id,code,name,decimal_places,whole_unit_only` |
| `POST items/import` | `CompanyId` + `Edit` | Existing 11-column item header, optionally followed by `supplier_external_id` |

For scoped requests, send `CompanyId` beside `Csv` and `DryRun`, for example:

```json
{
  "companyId": 42,
  "dryRun": true,
  "csv": "external_id,code,name,decimal_places,whole_unit_only\nkg,KG,Kilogram,3,false"
}
```

The examples below are synthetic and contain no credentials or customer data:

```csv
external_id,code,legal_name,trading_name,registration_number,tax_identifier,base_currency,country_code,currency_scale,is_active
company-demo,DEMO,Example Trading,Example,,SYNTHETIC-TAX,QAR,QA,2,true
```

```csv
external_id,code,name,address,time_zone_id,is_active
branch-demo,MAIN,Example Main Branch,,Asia/Qatar,true
```

```csv
external_id,branch_external_id,name,address
location-demo,branch-demo,Example Warehouse,
```

Use the company import first, then branch, location, supplier/unit, and item imports with
the returned company ID. Opening financial balances remain a separately approved workflow.
