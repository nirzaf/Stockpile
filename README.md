# Inventory Management System

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10-purple.svg)](https://dotnet.microsoft.com/)
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-16-blue.svg)](https://www.postgresql.org/)
[![Docker](https://img.shields.io/badge/Docker-ready-2496ED.svg)](https://www.docker.com/)

A modern inventory management web application for tracking items, stock levels, purchase orders, suppliers, and locations. Built with Clean Architecture, CQRS, and a mobile-first UI — runs anywhere.

## Documentation

- **🌐 [Live docs site](https://nirzaf.github.io/InventoryManagementSystem/)** — published via GitHub Pages from the `docs/` folder. The recommended place for end users.
- **[User Guide](USER_GUIDE.md)** — for the people who will *use* the application day-to-day (login, items, stock operations, purchase orders, troubleshooting).
- **README.md** (this file) — for developers and operators: installation, architecture, API, deployment.

> To enable the GitHub Pages site on your fork: **Settings → Pages → Source: `master` (or `main`) branch, `/docs` folder → Save**. The site is built automatically with Jekyll.

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Runtime | .NET 10, ASP.NET Core MVC |
| Database | PostgreSQL 16 + Entity Framework Core 10 |
| UI | MudBlazor 9 (responsive, no Bootstrap) |
| CQRS | MediatR 12 |
| Auth | ASP.NET Core Identity (RBAC) |
| Reports | QuestPDF |
| AI / ML | ML.NET — demand forecasting + anomaly detection |
| Logging | Serilog (console + rolling file) |
| API | RESTful with Asp.Versioning.Mvc (URL + header) |
| Containerization | Docker + Docker Compose |
| Testing | xUnit, Moq, FluentAssertions, AutoFixture, EF Core InMemory |
| CI/CD | GitHub Actions + GHCR |

## Features

**Inventory Management**
- Full CRUD for items, suppliers, locations, and purchase orders
- Stock operations: receive, transfer between locations, sell
- Complete transaction history with filtering by date

**Headless API**
- Versioned RESTful API (`/api/v1/items`, `/api/v1/stock`, `/api/v1/forecast`, `/api/v1/anomalies`)
- MediatR-powered minimal endpoints
- URL segment and header-based versioning

**AI-Powered Insights**
- Demand forecasting per item using ML.NET SSA time-series analysis
- Anomaly detection for unusual stock movements (spike/drop detection)
- Runs locally — zero cloud dependencies

**Role-Based Access**
- Admin, Manager, and Staff roles
- Secure login with ASP.NET Core Identity

**Mobile-First UI**
- MudBlazor component library for responsive design
- Works on desktop, tablet, and mobile browsers

**PDF Reports**
- QuestPDF for generating purchase orders and stock reports

## Quick Start

### Docker (recommended)

```bash
git clone https://github.com/nirzaf/InventoryManagementSystem.git
cd InventoryManagementSystem
cp .env.example .env        # edit credentials if desired
docker compose up -d        # starts app + PostgreSQL
```

The app will be available at **http://localhost:8080**.

Swagger UI is available at **http://localhost:8080/swagger** in the Development environment for interactive API exploration.

Default admin: `admin@inventory.com` / `Admin@123` (change in `.env`).

### Manual Setup

**Prerequisites:** .NET 10 SDK, PostgreSQL 16+

```bash
# 1. Create the database
createdb InventoryDB

# 2. Set the connection string
export ConnectionStrings__DefaultConnection="Host=localhost;Database=InventoryDB;Username=postgres;Password=yourpassword"

# 3. Run migrations and start
cd InventoryManagementSystem.Web
dotnet run
```

Open **https://localhost:5001** in your browser.

## API Reference

All endpoints are prefixed with `/api/v1`.

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/items` | List all items |
| `GET` | `/items/{id}` | Get item by ID |
| `GET` | `/stock` | List all stock in hand |
| `POST` | `/stock/receive` | Receive stock |
| `GET` | `/forecast/{itemId}` | Demand forecast for an item |
| `GET` | `/forecast` | Forecast all items |
| `GET` | `/anomalies` | Detect stock anomalies |

## Project Structure

```
InventoryManagementSystem.Web/          # ASP.NET Core MVC + API
├── Controllers/                        # MVC controllers (Items, Stock, Suppliers, etc.)
│   └── Api/V1/                         # Versioned API controllers
├── Views/                              # MudBlazor Razor views
├── Program.cs                          # App entry point + DI configuration

InventoryManagementSystem.Core/         # Domain layer
├── Entities/                           # Item, StockTransaction, Supplier, Location, etc.
├── Interfaces/                         # IItemService, IStockService, IUnitOfWork, etc.
├── Services/                           # Business logic + ML.NET AI services
├── Features/                           # MediatR CQRS (Commands, Queries, Handlers)
└── Models/                             # DTOs (DemandForecastResult, StockAnomaly)

InventoryManagementSystem.Infrastructure/ # Data access
├── Data/                               # DbContext, migrations, seed data
└── Repositories/                       # Generic Repository<T> implementation

InventoryManagementSystem.Tests/        # xUnit test suite
├── Core/Services/                      # Service unit tests
├── Core/Handlers/                      # MediatR handler tests
├── Web/Controllers/                    # Controller tests
└── Integration/                        # Integration tests (WebApplicationFactory)
```

## Development

```bash
dotnet build                              # build solution
dotnet test                               # run all tests
dotnet ef migrations add MigrationName    # add migration
  --project InventoryManagementSystem.Infrastructure
  --startup-project InventoryManagementSystem.Web
```

## Deployment

```bash
# Publish
dotnet publish -c Release -o ./publish

# Docker Compose (production)
cp .env.example .env && docker compose up -d

# Automated deployment script
./scripts/deploy.sh --build
```

The CI pipeline (`.github/workflows/ci.yml`) builds, tests, and pushes a Docker image to GitHub Container Registry on every push to `master`.

## Contributing

Pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines and [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) for community standards.

## License

MIT — see [LICENSE](LICENSE). Use, modify, distribute, and sell freely.

## Acknowledgements

Built on the shoulders of open source: [.NET](https://github.com/dotnet), [PostgreSQL](https://www.postgresql.org/), [MudBlazor](https://mudblazor.com/), [MediatR](https://github.com/jbogard/MediatR), [QuestPDF](https://www.questpdf.com/), [Serilog](https://serilog.net/), [ML.NET](https://dotnet.microsoft.com/apps/machinelearning-ai/ml-dotnet), [xUnit](https://xunit.net/), and many more.

For end-user documentation, see [USER_GUIDE.md](USER_GUIDE.md).
