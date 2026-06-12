# Migration Progress Summary

_Last reviewed: 2026-06-12_

The Inventory Management System has been migrated from a legacy Windows Forms / SQL Server
application to a modern .NET 10 web stack. This document tracks the current state of the
migration. It is reviewed periodically and updated when major milestones change.

## Stack

- **Runtime:** .NET 10, ASP.NET Core MVC
- **Database:** PostgreSQL 16 with Entity Framework Core 10
- **UI:** MudBlazor 9 (responsive, mobile-first, no Bootstrap)
- **CQRS:** MediatR 12
- **Auth:** ASP.NET Core Identity (Admin / Manager / Staff roles)
- **API:** Versioned REST endpoints under `/api/v1`
- **AI / ML:** ML.NET — demand forecasting and anomaly detection
- **Reports:** QuestPDF
- **Logging:** Serilog (console + rolling files)
- **Containerization:** Docker + Docker Compose
- **CI/CD:** GitHub Actions + GHCR

## Progress

| Area | Status | Notes |
|------|--------|-------|
| Architecture setup | 100% | Four-project Clean Architecture (Web / Infrastructure / Core / Tests) |
| Domain model | 100% | All entities, enums, and value objects in `Core/Entities` |
| Data access | 100% | `InventoryDbContext`, generic `Repository<T>`, `UnitOfWork`, EF Core migrations, seed data |
| Business logic | 100% | Service layer (`ItemService`, `StockService`, `PurchaseOrderService`, `SupplierService`, `LocationService`, ML services) |
| Web layer | 100% | MVC controllers, Razor views, versioned API controllers, Swagger UI, MudBlazor layout |
| AI / ML | 100% | ML.NET SSA forecasting + IID spike/drop anomaly detection |
| Testing | ~80% | 184 xUnit tests covering services, handlers, controllers, and integration paths |
| Documentation | 100% | README, CONTRIBUTING, SECURITY, LICENSE, CHANGELOG, XML doc comments |

**Overall: ~90% complete.** Remaining work is incremental: expanding test coverage toward 100%
on edge cases, additional commercial features behind feature flags, and ongoing dependency
updates.

## Completed Milestones

- Clean Architecture solution with 4 projects
- PostgreSQL + EF Core 10 with `xmin` optimistic concurrency
- Identity-based RBAC with three seeded roles
- Stock operations: receive, transfer, sell with full transaction history
- Versioned REST API (`/api/v1/items`, `/api/v1/stock`, `/api/v1/forecast`, `/api/v1/anomalies`)
- Swagger UI in the Development environment
- MudBlazor responsive UI across desktop, tablet, and mobile
- QuestPDF report generation for purchase orders and stock
- ML.NET SSA-based demand forecasting per item
- ML.NET IID-based anomaly detection (spike / drop)
- Webhook dispatcher for outbound event notifications
- Audit log table populated automatically on every save
- Global exception handler with structured JSON error responses
- Health checks for liveness and database connectivity
- Rate limiting for the API and AI endpoints
- Docker + Docker Compose deployment with PostgreSQL
- GitHub Actions CI: build, test, push image to GHCR
- Open-source governance: LICENSE, CODE_OF_CONDUCT, CONTRIBUTING, SECURITY, issue / PR templates

## Useful Commands

```bash
# Build solution
dotnet build

# Run application
cd InventoryManagementSystem.Web
dotnet run

# Add migration
dotnet ef migrations add MigrationName \
  --project InventoryManagementSystem.Infrastructure \
  --startup-project InventoryManagementSystem.Web

# Update database
dotnet ef database update \
  --project InventoryManagementSystem.Infrastructure \
  --startup-project InventoryManagementSystem.Web

# Run tests
dotnet test
```

## Notes

- The codebase is the system of record — if this document drifts from reality, trust the code.
- The legacy Windows Forms and SQL Server artefacts are no longer part of the active solution
  and are retained only for historical reference.
