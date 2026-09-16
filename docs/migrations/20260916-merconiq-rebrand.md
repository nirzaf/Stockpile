# Merconiq rebrand

Merconiq is the new platform identity for the project formerly developed as
Stockpile / Inventory Management System. This release changes product naming,
.NET project identities, namespaces, runtime identifiers, container metadata,
and documentation. Inventory remains the name of the inventory business
domain.

## Upgrade notes

This is a pre-production cutover. No compatibility bridge is provided for the
old product identifiers:

- Configure new local ASP.NET User Secrets under `Merconiq-Development`.
- Existing local Data Protection cookies are not expected to remain valid after
  the application discriminator changes to `Merconiq`.
- Use the new `merconiq-dark-mode` browser preference key.
- Use `ghcr.io/nirzaf/merconiq` for new container pulls.
- Existing API resource routes and `/api/v1` versioning are unchanged.

The rebrand does not add, remove, or rewrite EF Core migrations, database
tables, entity keys, or business behavior. Persistent database and Data
Protection volumes must still be supplied by the deployment environment.

## Observability

The OpenTelemetry service is now `Merconiq` and the inventory meter is
`Merconiq.Inventory`. Dashboards and alerts that refer to the former product
names must be updated as part of the cutover.

Historical commits, issues, pull requests, and release records retain their
original names as repository history.
