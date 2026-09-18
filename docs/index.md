---
layout: default
title: Merconiq — Documentation
description: End-user guide and documentation for the Merconiq.
---

# 📦 Merconiq — Documentation

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/nirzaf/merconiq/blob/master/LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10-purple.svg)](https://dotnet.microsoft.com/)
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-16-blue.svg)](https://www.postgresql.org/)
[![Docker](https://img.shields.io/badge/Docker-ready-2496ED.svg)](https://www.docker.com/)

Welcome to the documentation site for **Merconiq** — a modern inventory management web app for tracking items, stock levels, purchase orders, suppliers, and locations.

> The canonical documentation URL is [GitHub Pages](https://nirzaf.github.io/merconiq/). The Pages workflow renders these Markdown files with Jekyll; see [repository and artifact cutover status](REPOSITORY_CUTOVER.md) for deployment evidence and any remaining verification gates.

---

## 📖 Available guides

| Guide | Audience | Description |
|-------|----------|-------------|
| **[User Guide](USER_GUIDE.md)** | End users (Admin, Manager, Staff) | How to use the app day-to-day: login, items, stock operations, purchase orders, troubleshooting. |
| **[Stranded transfer transit runbook](TRANSFER_TRANSIT_RUNBOOK.md)** | Inventory operators | Investigate and resolve supported in-transit quantities using the existing API and auditable evidence. |
| **[README](https://github.com/nirzaf/merconiq/blob/master/README.md)** | Developers & operators | Installation, architecture, API reference, deployment. |
| **[CHANGELOG](https://github.com/nirzaf/merconiq/blob/master/CHANGELOG.md)** | Everyone | Version history and release notes. |
| **[Contributing](https://github.com/nirzaf/merconiq/blob/master/CONTRIBUTING.md)** | Contributors | How to file issues, open PRs, and follow the project's coding standards. |
| **[Security](https://github.com/nirzaf/merconiq/blob/master/SECURITY.md)** | Operators | Security policy and how to report vulnerabilities. |
| **[Funding and OSS Eligibility](FUNDING_ELIGIBILITY.md)** | Maintainers | Source-dated public ledger for OSS programs, owner gates, and provider confirmation status. |
| **[Codex for Open Source application draft](FUNDING_APPLICATION_DRAFT.md)** | Maintainers | Public-safe, unsubmitted form answers; private applicant fields remain blank. |
| **[Funding application evidence index](FUNDING_APPLICATION_EVIDENCE.md)** | Maintainers | Dated program/repository sources, unknowns, measurement plan, and owner gates. |
| **[Public GitHub evidence](GITHUB_EVIDENCE.md)** | Maintainers | Export dated, query-traceable maintenance signals with explicit unknowns. |
| **[Readiness baseline](READINESS_BASELINE.md)** | Maintainers | Current SHA, SPF/SFR reconciliation, source checks, and milestone dependencies. |
| **[Organization ownership](ORGANIZATION_OWNERSHIP.md)** | Maintainers | Tenant, company, branch, location and document ownership, including unknown legacy mappings. |
| **[Architecture boundaries](ARCHITECTURE.md)** | Developers | Modular-monolith ownership, dependency direction, and extension rules. |
| **[Repository and artifact cutover](REPOSITORY_CUTOVER.md)** | Maintainers & operators | Canonical links, current Pages reachability, GHCR evidence, legacy references, and Compose volume precautions. |

---

## 🚀 Quick start

The fastest way to try Merconiq is with Docker:

```bash
git clone https://github.com/nirzaf/merconiq.git
cd merconiq
cp .env.example .env        # set database and JWT values for local evaluation
./scripts/validate-compose.sh development
docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d --wait
```

The app will be available at **http://localhost:8080**.
The development Compose file is selected explicitly; it enables Development
mode and publishes PostgreSQL only for local tooling. Set `DB_PASSWORD` and
`JWT_SECRET` before running the configuration check. Development startup does
not provision an administrator; use the explicit production bootstrap path for
that operation.

Configure the database password and JWT signing key in the untracked `.env` before
running the local development command. The development path does not provision an
administrator; use the explicit production bootstrap path for that operation. No
default credentials are committed.
For production, set the explicit bootstrap administrator credentials in `.env` and follow
the production migration and bootstrap commands in the README. Normal production web
startup does not seed users or sample data.

> For full installation, configuration, and deployment instructions, see the [README on GitHub](https://github.com/nirzaf/merconiq/blob/master/README.md).

---

## ✨ What can Merconiq do today?

- **Items** — full CRUD with codes, barcodes, prices, and suppliers.
- **Stock** — receive, transfer, and sell with a complete transaction history.
- **Purchase orders** — Pending → Approved → Received lifecycle, with status chips.
- **Suppliers & locations** — manage who you buy from and where you store stock.
- **Mobile-first UI** — works on desktop, tablet, and phone.
- **REST API** — versioned endpoints under `/api/v1` for integrations.
- **AI insights** — on-device demand forecasting and anomaly detection (ML.NET).
- **PDF & CSV reports** — generate purchase orders and export item lists.

---

## 🛠 Publishing status

The repository's [Pages workflow](https://github.com/nirzaf/merconiq/blob/master/.github/workflows/pages.yml)
builds the Markdown files in `docs/` into a Jekyll site, verifies the rendered
entry point, guide, stylesheet, and `/merconiq/` base path, then deploys the
generated `_site` artifact. See the [cutover guide](REPOSITORY_CUTOVER.md) for
the latest live URL and asset checks. A fork must configure and verify its own
Pages source and project base path; do not assume this repository's settings
apply to it.

---

## 🧭 Where to next?

- **I'm using the app for the first time** → start with the [User Guide](USER_GUIDE.md).
- **I'm installing or operating the app** → read the [README](https://github.com/nirzaf/merconiq/blob/master/README.md).
- **I want to integrate with the API** → see the [API Reference](https://github.com/nirzaf/merconiq/blob/master/README.md#api-reference).
- **I want to report a bug or suggest a feature** → open an [issue](https://github.com/nirzaf/merconiq/issues/new).
- **I want to contribute code** → read [CONTRIBUTING.md](https://github.com/nirzaf/merconiq/blob/master/CONTRIBUTING.md).

---

*Built with .NET 10, ASP.NET Core, MudBlazor, PostgreSQL, MediatR, and ML.NET.
Merconiq-authored material is released under the [MIT License](https://github.com/nirzaf/merconiq/blob/master/LICENSE);
third-party terms and notices are listed in the [third-party inventory](THIRD_PARTY_NOTICES.md).*
