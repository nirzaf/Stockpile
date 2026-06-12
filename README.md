# Stockpile

> Inventory Management System for Supermarkets and Small Shops

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10-purple.svg)](https://dotnet.microsoft.com/)
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-16-blue.svg)](https://www.postgresql.org/)
[![Docker](https://img.shields.io/badge/Docker-ready-2496ED.svg)](https://www.docker.com/)

A modern inventory management web application for tracking items, stock levels, purchase orders, suppliers, and locations. Built with Clean Architecture, CQRS, and a mobile-first UI — runs anywhere.

## Documentation

- **🌐 [Live docs site](https://nirzaf.github.io/Stockpile/)** — published via GitHub Pages from the `docs/` folder. The recommended place for end users.
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
git clone https://github.com/nirzaf/Stockpile.git
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

## CI/CD

Every push and pull request to `master` runs an automated pipeline, and tagged releases publish Docker images to GitHub Container Registry and cut a GitHub Release.

### Workflows

| Workflow | File | Trigger | Purpose |
|----------|------|---------|---------|
| **CI** | [`.github/workflows/ci.yml`](.github/workflows/ci.yml) | PR + push to `master` | Restore → build → run xUnit tests with coverage → upload `coverage-report` artifact. |
| **Docker** | [`.github/workflows/docker.yml`](.github/workflows/docker.yml) | Push to `master` & `v*.*.*` tags | Multi-arch build (`linux/amd64`, `linux/arm64`) → push to `ghcr.io/nirzaf/inventorymanagementsystem` with `latest`, `sha-…`, and semver tags. |
| **GitHub Pages** | [`.github/workflows/pages.yml`](.github/workflows/pages.yml) | Push to `master` (when `docs/**` changes) | Deploys the `/docs` folder to `https://nirzaf.github.io/Stockpile/`. |
| **Release** | [`.github/workflows/release.yml`](.github/workflows/release.yml) | Push of `v*.*.*` tag | Cuts a GitHub Release with auto-generated changelog and Docker pull instructions. |
| **Dependabot** | [`.github/dependabot.yml`](.github/dependabot.yml) | Weekly (Mon) | Opens grouped PRs for NuGet, GitHub Actions, and Docker base-image updates. |

### Release flow

1. Bump versions as needed and merge to `master`. The CI and Docker workflows run.
2. When ready to release, create and push a semver tag:
   ```bash
   git tag v1.2.3
   git push origin v1.2.3
   ```
3. The **Release** workflow creates a GitHub Release with a changelog derived from commits since the last tag, and the **Docker** workflow publishes the multi-arch image with tags `v1.2.3`, `1.2`, `1`, and `latest`.

### GitHub Pages

The Pages site is built automatically from the `docs/` folder. **One-time setup on a fresh repo:** go to **Settings → Pages → Source: GitHub Actions** and save. After that, every change under `docs/**` (or to `pages.yml`) is deployed within ~1 minute. The site is available at `https://nirzaf.github.io/Stockpile/`.

### Coverage

Test coverage is collected via `coverlet.collector` and uploaded as a build artifact named `coverage-report` (Cobertura XML). Download it from the Actions run to inspect line/branch coverage locally or pipe it into a future Codecov integration.

### Required secrets

All workflows use the default `GITHUB_TOKEN` and require no additional secrets.

### Recommended branch protection

For `master`: require PR + 1 approval, require status checks `build-and-test` and `Docker`, require linear history, and disallow force pushes.

## Contributing

Pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines and [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) for community standards.

## License

MIT — see [LICENSE](LICENSE). Use, modify, distribute, and sell freely.

## Acknowledgements

Built on the shoulders of open source: [.NET](https://github.com/dotnet), [PostgreSQL](https://www.postgresql.org/), [MudBlazor](https://mudblazor.com/), [MediatR](https://github.com/jbogard/MediatR), [QuestPDF](https://www.questpdf.com/), [Serilog](https://serilog.net/), [ML.NET](https://dotnet.microsoft.com/apps/machinelearning-ai/ml-dotnet), [xUnit](https://xunit.net/), and many more.

For end-user documentation, see [USER_GUIDE.md](USER_GUIDE.md).
