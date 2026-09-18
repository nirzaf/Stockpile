# Merconiq

> Open-source business management platform evolving from inventory and procurement into a modular ERP.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10-purple.svg)](https://dotnet.microsoft.com/)
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-16-blue.svg)](https://www.postgresql.org/)
[![Docker](https://img.shields.io/badge/Docker-ready-2496ED.svg)](https://www.docker.com/)

Merconiq is an open-source business management platform built with .NET and PostgreSQL. It is evolving from a robust inventory and procurement foundation toward a modular ERP platform for small and growing businesses.

## Documentation

- **[Documentation source and cutover status](docs/REPOSITORY_CUTOVER.md)** — the selected [GitHub Pages URL](https://nirzaf.github.io/merconiq/) currently redirects to a 404; see the verified status and owner-pending repair gate.
- **[User Guide](docs/USER_GUIDE.md)** — for the people who will *use* the application day-to-day (login, items, stock operations, purchase orders, troubleshooting).
- **README.md** (this file) — for developers and operators: installation, architecture, API, deployment.

> To enable the GitHub Pages site on your fork: **Settings → Pages → Source: `master` (or `main`) branch, `/docs` folder → Save**. The site is built automatically with Jekyll.

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Runtime | .NET SDK 10.0.300, ASP.NET Core MVC |
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

## Available today

## Repository Layout

The repository contains only the maintained web application and its supporting projects:

- `Merconiq.Core` — domain entities, services, validators, and CQRS handlers
- `Merconiq.Infrastructure` — EF Core persistence and integrations
- `Merconiq.Web` — ASP.NET Core API and Blazor UI
- `Merconiq.Tests` — unit and integration tests

The former `Merconiq/` WinForms source tree was removed from the repository;
it is not part of the supported build or deployment path.

**Inventory Management**
- Full CRUD for items, suppliers, locations, and purchase orders
- Stock operations: receive, transfer between locations, sell
- Complete transaction history with filtering by date

**Headless API**
- Versioned RESTful API (`/api/v1/items`, `/api/v1/stock`, `/api/v1/forecast`, `/api/v1/anomalies`)
- MediatR-powered minimal endpoints
- URL segment and header-based versioning

**AI-Powered Insights**
- Demand forecasting per item using the configured implementation (managed moving average by default; ML.NET SSA is an explicit opt-in)
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

## ERP roadmap

Merconiq's planned platform domains include Accounting, CRM, Sales, Human
Resources, Payroll, Manufacturing, Assets, Projects, Point of Sale, and
Integrations. These are roadmap areas, not claims of currently implemented
functionality.

## Quick Start

### Docker local development

```bash
git clone https://github.com/nirzaf/merconiq.git
cd merconiq
cp .env.example .env        # edit credentials if desired
./scripts/validate-compose.sh development
docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d --wait
```

The app will be available at **http://localhost:8080**.
The development file is explicit: it enables Development mode, publishes the
local PostgreSQL port, mounts source directories, and enables the optional
watch configuration. Set `DB_PASSWORD`, `JWT_SECRET`, `ADMIN_EMAIL`, and
`ADMIN_PASSWORD` in `.env` when using the Development seed administrator;
`validate-compose.sh` validates required database/JWT values without printing
the resolved secrets.

Swagger UI is available at **http://localhost:8080/swagger** in the Development environment for interactive API exploration.

Set `DB_PASSWORD` and `JWT_SECRET` in `.env` before starting. There are no committed default credentials.

### Manual Setup

**Prerequisites:** .NET SDK 10.0.300 exactly (the repository pins this in `global.json`), PostgreSQL 16+, and Docker Desktop in Linux-container mode when running the PostgreSQL integration phase.

```bash
# 1. Create the database
createdb InventoryDB

# 2. Set the connection string and JWT secret through your shell environment
export ConnectionStrings__DefaultConnection="Host=localhost;Database=InventoryDB;Username=postgres;Password=$DB_PASSWORD"
export JwtSettings__Secret="$JWT_SECRET"

# 3. Apply migrations and start the development application
dotnet ef database update --project src/Merconiq.Infrastructure --startup-project src/Merconiq.Web
cd src/Merconiq.Web
dotnet run
```

Open the HTTP URL printed by `dotnet run` (this repository's launch profile uses `http://localhost:5069`; HTTPS is available only when the local development certificate is configured).

For local Development runs, configure the database connection and JWT secret with ASP.NET User Secrets instead of committing them:

```bash
dotnet user-secrets init --project src/Merconiq.Web
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Database=InventoryDB;Username=postgres;Password=<local-password>" --project src/Merconiq.Web
dotnet user-secrets set "JwtSettings:Secret" "<at-least-32-byte-local-secret>" --project src/Merconiq.Web
dotnet user-secrets set "BootstrapAdmin:TenantId" "default" --project src/Merconiq.Web
dotnet user-secrets set "BootstrapAdmin:Email" "<admin-email>" --project src/Merconiq.Web
dotnet user-secrets set "BootstrapAdmin:Password" "<admin-password>" --project src/Merconiq.Web
```

Run the one-shot bootstrap before the first login, then start the web application normally:

```bash
dotnet run --project src/Merconiq.Web -- --bootstrap-admin
dotnet run --project src/Merconiq.Web
```

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
| `POST` | `/organization/companies/import` | Dry-run or apply tenant company master import (tenant Admin required) |
| `POST` | `/organization/branches/import` | Dry-run or apply company-scoped branch import (Edit capability required) |
| `POST` | `/organization/locations/import` | Dry-run or apply company-scoped location import (Administer capability required) |
| `POST` | `/organization/suppliers/import` | Dry-run or apply company-scoped shared supplier import (Edit capability required) |
| `POST` | `/organization/units/import` | Dry-run or apply company-scoped shared UOM import (tenant Admin and CompanyId required) |
| `POST` | `/organization/items/import` | Dry-run or apply a company-scoped shared item master CSV import (Edit capability required) |

Master-data imports default to dry-run and use the existing bearer-token API. Every request
except company creation includes an explicit `CompanyId`; it is an authorization and onboarding
scope, not an ownership column for shared items, suppliers, or units. Every import requires stable
`external_id` values, reports row-level `created`, `unchanged`, or `rejected` results, and applies
no rows when any row is rejected. A valid apply can be replayed safely. CSV headers and synthetic
examples are documented in [`MASTER_DATA_ONBOARDING.md`](docs/MASTER_DATA_ONBOARDING.md).

## Project Structure

```
src/Merconiq.Web/          # ASP.NET Core MVC + API
├── Controllers/                        # MVC controllers (Items, Stock, Suppliers, etc.)
│   └── Api/V1/                         # Versioned API controllers
├── Views/                              # MudBlazor Razor views
├── Program.cs                          # App entry point + DI configuration

src/Merconiq.Core/         # Domain layer
├── Entities/                           # Item, StockTransaction, Supplier, Location, etc.
├── Interfaces/                         # IItemService, IStockService, IUnitOfWork, etc.
├── Services/                           # Business logic + ML.NET AI services
├── Features/                           # MediatR CQRS (Commands, Queries, Handlers)
└── Models/                             # DTOs (DemandForecastResult, StockAnomaly)

src/Merconiq.Infrastructure/ # Data access
├── Data/                               # DbContext, migrations, seed data
└── Repositories/                       # Generic Repository<T> implementation

tests/Merconiq.Tests/        # xUnit test suite
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
  --project src/Merconiq.Infrastructure
  --startup-project src/Merconiq.Web
```

### Multi-operation transactions

Services that perform multiple persistence operations as one business action can use the
`IUnitOfWork` transaction boundary:

```csharp
await unitOfWork.BeginTransactionAsync(cancellationToken);
try
{
    // Add or update entities through the repositories.
    await unitOfWork.CommitTransactionAsync(cancellationToken);
}
catch
{
    await unitOfWork.RollbackTransactionAsync(cancellationToken);
    throw;
}
```

`CommitTransactionAsync` saves pending changes before committing and rolls back on failure.
The normal `SaveChangesAsync` path remains available for single-operation service methods.

## Deployment

The Docker Compose production path is deliberately explicit and does not load a
development override. It keeps PostgreSQL on the private Compose network (no
host port is published), preserves the `pgdata` and `dataprotection` volumes,
and requires non-empty database and JWT secrets:

```bash
cp .env.example .env
# Edit .env: DB_PASSWORD and JWT_SECRET.
./scripts/validate-compose.sh production
docker compose -f docker-compose.yml --profile migrations run --rm migrator
docker compose -f docker-compose.yml --profile bootstrap run --rm bootstrap-admin
docker compose -f docker-compose.yml up -d --wait
```

Before the bootstrap command, set `BOOTSTRAP_ADMIN_TENANT`, `BOOTSTRAP_ADMIN_EMAIL`,
and `BOOTSTRAP_ADMIN_PASSWORD` in the untracked `.env` file. The command is explicit,
tenant-bound, safe to repeat, and fails if Identity rejects the supplied credentials.
Normal production web startup does not create users, roles, sample locations, or other
sample data. The migrator applies the committed schema before the bootstrap command and
application start; never commit the administrator password or data-protection keys.
Keep the `dataprotection` volume across restarts so existing sessions and protected
values retain their documented behavior.

```bash
# Publish
dotnet publish -c Release -o ./publish

# Docker Compose (production; the script selects docker-compose.yml explicitly)
cp .env.example .env    # set DB_PASSWORD, JWT_SECRET, and one-shot bootstrap settings
./scripts/deploy.sh --migrate

# Automated deployment script
./scripts/deploy.sh --build --migrate
```

The production deployment script applies committed EF migrations through a one-shot SDK migrator before starting the runtime container. The web process does not run migrations on production startup. The CI pipeline (`.github/workflows/ci.yml`) builds, tests, and pushes a Docker image to GitHub Container Registry on every push to `master`.

## CI/CD

Every push and pull request to `master` runs an automated pipeline, and tagged releases publish Docker images to GitHub Container Registry and cut a GitHub Release.

### Workflows

| Workflow | File | Trigger | Purpose |
|----------|------|---------|---------|
| **CI** | [`.github/workflows/ci.yml`](.github/workflows/ci.yml) | PR + push to `master` | Restore → build → run xUnit tests with coverage → upload `coverage-report` artifact. |
| **Docker** | [`.github/workflows/docker.yml`](.github/workflows/docker.yml) | Push to `master` & `v*.*.*` tags; manual candidate validation | Resolves and revalidates one exact commit, then builds the multi-arch image (`linux/amd64`, `linux/arm64`) → configured name `ghcr.io/nirzaf/merconiq` with commit-specific `sha-<full-commit>` and branch/semver tag aliases. Verify the package and digest before use; tags are pointers. Manual dry runs do not log in or push. |
| **GitHub Pages** | [`.github/workflows/pages.yml`](.github/workflows/pages.yml) | Push to `master` (when `docs/**` changes) | Uploads the `/docs` folder for Pages deployment. The selected URL currently redirects to an external 404; see the [cutover status](docs/REPOSITORY_CUTOVER.md). |
| **Release** | [`.github/workflows/release.yml`](.github/workflows/release.yml) | Push of `v*.*.*` tag; manual existing-tag validation | Revalidates the exact tag commit, waits for the commit-specific SHA image tag and semver image tag to exist, then cuts a GitHub Release. Use the verified manifest digest as the immutable image identity. Manual dry runs do not create a release. |
| **Dependabot** | [`.github/dependabot.yml`](.github/dependabot.yml) | Weekly (Mon) | Opens grouped PRs for NuGet, GitHub Actions, and Docker base-image updates. |

### Release flow

1. Bump versions as needed and merge to `master`. The CI and Docker workflows run.
2. When ready to release, create and push a semver tag:
   ```bash
   git tag v1.2.3
   git push origin v1.2.3
   ```
3. The **Release** workflow revalidates the tag’s exact commit and verifies the Docker workflow’s commit-specific `sha-<full-commit>` and `v1.2.3` image tags before creating a GitHub Release. The Docker workflow publishes the multi-arch image with `sha-<full-commit>`, `v1.2.3`, `1.2`, and `1`; `latest` is reserved for `master`. Registry tags are pointers; retain the successful run's manifest digest for an immutable image reference.

To exercise either workflow without publication, use its manual `dry_run` input. A manual Docker candidate may be a branch or existing tag; a manual Release candidate must be an existing `vMAJOR.MINOR.PATCH` tag. Both reject malformed refs and stale remote candidates.

### GitHub Pages

The Pages workflow uploads the `docs/` folder when a matching change reaches `master`. On a fresh repository, the owner must enable **Settings → Pages → Source: GitHub Actions**. A successful deployment does not guarantee the public URL is reachable: as of 2026-09-18, `https://nirzaf.github.io/merconiq/` redirects to a destination returning 404. See the [repository and artifact cutover guide](docs/REPOSITORY_CUTOVER.md); do not rely on the live site until the redirect is repaired and verified.

### Coverage

Test coverage is collected via `coverlet.collector` and uploaded as a build artifact named `coverage-report` (Cobertura XML). Download it from the Actions run to inspect line/branch coverage locally or pipe it into a future Codecov integration.

### Required secrets

All workflows use the default `GITHUB_TOKEN` and require no additional secrets.

### Recommended branch protection

For `master`: require PR + 1 approval, require status checks `build-and-test` and `Docker`, require linear history, and disallow force pushes.

## Contributing

Pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines and [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) for community standards.

## License

Merconiq-authored material is available under the MIT License; see
[LICENSE](LICENSE). Third-party packages and copied browser assets retain
their own licenses and notices. Review [third-party notices](docs/THIRD_PARTY_NOTICES.md)
before redistributing a built artifact; the repository MIT license does not
relicense those components.

## Acknowledgements

Built on the shoulders of open source: [.NET](https://github.com/dotnet), [PostgreSQL](https://www.postgresql.org/), [MudBlazor](https://mudblazor.com/), [MediatR](https://github.com/jbogard/MediatR), [QuestPDF](https://www.questpdf.com/), [Serilog](https://serilog.net/), [ML.NET](https://dotnet.microsoft.com/apps/machinelearning-ai/ml-dotnet), [xUnit](https://xunit.net/), and many more.

For end-user documentation, see [USER_GUIDE.md](USER_GUIDE.md).
