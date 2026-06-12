# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-06-12

### Added

- Clean Architecture solution with four projects: `Web`, `Infrastructure`, `Core`, and `Tests`.
- PostgreSQL 16 data layer with Entity Framework Core 10 and `xmin` optimistic concurrency.
- Domain entities for items, stock in hand, locations, suppliers, purchase orders, order
  details, stock transactions, audit log, and webhook subscriptions.
- Generic `Repository<T>` and `UnitOfWork` patterns.
- Service layer: `ItemService`, `StockService`, `PurchaseOrderService`, `SupplierService`,
  `LocationService`, `DemandForecastService`, and `AnomalyDetectionService`.
- MediatR-based CQRS handlers under `Core/Features` for commands and queries.
- ASP.NET Core Identity with `Admin`, `Manager`, and `Staff` roles and seeded admin user.
- Versioned REST API under `/api/v1` for items, stock, forecasting, and anomalies.
- Swagger UI in the Development environment with XML doc integration.
- MudBlazor 9 responsive UI — desktop, tablet, and mobile.
- ML.NET SSA demand forecasting per item with moving-average fallback.
- ML.NET IID spike / drop anomaly detection over stock transactions.
- QuestPDF report generation for purchase orders and stock reports.
- Webhook dispatcher for outbound event notifications.
- Automatic audit log population on every `SaveChangesAsync`.
- Global exception handler returning structured JSON error responses.
- Health checks for liveness and database connectivity.
- Rate limiting — 100 req/min for the API, 10 req/min for AI endpoints.
- Dual authentication: cookie auth for the MVC UI and JWT bearer for API calls.
- Docker and Docker Compose deployment with PostgreSQL.
- GitHub Actions CI: build, test, and publish image to GitHub Container Registry.
- Open-source governance files: `LICENSE`, `CODE_OF_CONDUCT.md`, `CONTRIBUTING.md`,
  `SECURITY.md`, issue and PR templates.
- 184 xUnit tests covering services, handlers, controllers, and integration paths.
