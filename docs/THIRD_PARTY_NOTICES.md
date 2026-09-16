# Third-party dependency and redistribution notices

This inventory records the dependency and copied-asset evidence visible at
Stockpile baseline `b1646978f51ac7493d70e1b701685d0e8fd7b7f2` (16 September
2026). It is an engineering notice and provenance record, not legal advice or
a conclusion about which license applies to a particular owner, operator, or
distribution model. The repository's [MIT License](../LICENSE) covers
Stockpile-authored material only; it does not relicense third-party material.

Before distributing a build, the owner should confirm the applicable terms for
the intended use and preserve the relevant package and asset notices. In
particular, the entries marked `owner review` must not be treated as cleared by
the repository MIT license.

## Direct NuGet references

The following 40 unique package references are declared by the four pinned
projects. `Production` means the package is referenced by the web or runtime
projects; `Build/test` means it is used only by the test project or as a build
tooling dependency. License labels are the SPDX expression reported by the
NuGet registration metadata where one exists. A package-provided license file
is authoritative when NuGet reports only a deprecated license URL.

| Package | Version | Project scope | Reported terms / notice source |
| --- | --- | --- | --- |
| Asp.Versioning.Mvc; Asp.Versioning.Mvc.ApiExplorer | 10.2.1 | Web | MIT ([NuGet license](https://licenses.nuget.org/MIT)) |
| AutoFixture | 4.18.1 | Build/test | MIT ([NuGet license](https://licenses.nuget.org/MIT)) |
| AutoMapper | 16.2.0 | Infrastructure | RPL 1.5 or the separate Lucky Penny Software license; see the package `LICENSE.md` and [license options](https://luckypennysoftware.com/license) (`owner review`) |
| ClosedXML | 0.105.0 | Web | MIT ([NuGet license](https://licenses.nuget.org/MIT)) |
| coverlet.collector | 10.0.1 | Build/test | MIT ([NuGet license](https://licenses.nuget.org/MIT)) |
| FluentAssertions | 8.10.0 | Tests | Xceed Fluent Assertions Community License for non-commercial use; commercial use requires owner review of the package `LICENSE` ([project](https://github.com/fluentassertions/fluentassertions)) |
| FluentValidation; FluentValidation.DependencyInjectionExtensions | 12.1.1 | Core | Apache-2.0 ([NuGet license](https://licenses.nuget.org/Apache-2.0)) |
| MediatR | 14.1.0 | Core | RPL 1.5 or the separate Lucky Penny Software license; see the package `LICENSE.md` and [license options](https://luckypennysoftware.com/license) (`owner review`) |
| Microsoft.AspNetCore.Authentication.JwtBearer; Microsoft.AspNetCore.Diagnostics.EntityFrameworkCore | 10.0.9 | Web | MIT ([NuGet license](https://licenses.nuget.org/MIT)) |
| Microsoft.AspNetCore.Identity.EntityFrameworkCore | 10.0.9 | Infrastructure, Web | MIT ([NuGet license](https://licenses.nuget.org/MIT)) |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.9 | Tests | MIT ([NuGet license](https://licenses.nuget.org/MIT)) |
| Microsoft.EntityFrameworkCore | 10.0.9 | Infrastructure | MIT ([NuGet license](https://licenses.nuget.org/MIT)) |
| Microsoft.EntityFrameworkCore.Design; Microsoft.EntityFrameworkCore.Tools | 10.0.9 | Infrastructure, Web (Design) | MIT ([NuGet license](https://licenses.nuget.org/MIT)) |
| Microsoft.EntityFrameworkCore.InMemory | 10.0.9 | Tests | MIT ([NuGet license](https://licenses.nuget.org/MIT)) |
| Microsoft.Extensions.Caching.Abstractions; Microsoft.Extensions.Identity.Stores; Microsoft.Extensions.Logging.Abstractions | 10.0.9 | Core | MIT ([NuGet license](https://licenses.nuget.org/MIT)) |
| Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore | 10.0.9 | Web | MIT ([NuGet license](https://licenses.nuget.org/MIT)) |
| Microsoft.ML; Microsoft.ML.TimeSeries | 5.0.0 | Core | MIT ([NuGet license](https://licenses.nuget.org/MIT)) |
| Microsoft.NET.Test.Sdk | 18.8.1 | Tests | MIT ([NuGet license](https://licenses.nuget.org/MIT)) |
| Moq | 4.20.72 | Tests | BSD-3-Clause ([NuGet license](https://licenses.nuget.org/BSD-3-Clause)) |
| MudBlazor | 9.5.0 | Web | MIT ([NuGet license](https://licenses.nuget.org/MIT)) |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.2 | Infrastructure | PostgreSQL License ([NuGet license](https://licenses.nuget.org/PostgreSQL)) |
| OpenTelemetry.Exporter.OpenTelemetryProtocol; OpenTelemetry.Extensions.Hosting; OpenTelemetry.Instrumentation.AspNetCore; OpenTelemetry.Instrumentation.EntityFrameworkCore; OpenTelemetry.Instrumentation.Http; OpenTelemetry.Instrumentation.Runtime | 1.18.0 (EntityFrameworkCore `1.18.0-beta.1`) | Web | Apache-2.0 ([NuGet license](https://licenses.nuget.org/Apache-2.0)) |
| QuestPDF | 2026.7.1 | Web | QuestPDF dual-license package; the package `LICENSE.md` and [pricing/licensing page](https://www.questpdf.com/pricing) require owner review |
| Serilog.AspNetCore | 10.0.0 | Web | Apache-2.0 ([NuGet license](https://licenses.nuget.org/Apache-2.0)) |
| Swashbuckle.AspNetCore | 10.2.3 | Web | MIT ([NuGet license](https://licenses.nuget.org/MIT)) |
| Testcontainers.PostgreSql | 4.15.0 | Tests | MIT ([NuGet license](https://licenses.nuget.org/MIT)) |
| xunit; xunit.runner.visualstudio | 2.9.3 / 4.0.0 | Tests | Apache-2.0 ([NuGet license](https://licenses.nuget.org/Apache-2.0)) |

The table preserves package identities and pinned versions while grouping
packages that share the same terms. The authoritative direct-reference
locations are the `PackageReference` entries in:

- `InventoryManagementSystem.Core/InventoryManagementSystem.Core.csproj`
- `InventoryManagementSystem.Infrastructure/InventoryManagementSystem.Infrastructure.csproj`
- `InventoryManagementSystem.Tests/InventoryManagementSystem.Tests.csproj`
- `InventoryManagementSystem.Web/InventoryManagementSystem.Web.csproj`

## Transitive NuGet dependencies

This baseline does not contain a `packages.lock.json`; transitive versions are
therefore resolved by NuGet during restore and must not be guessed or copied
from a later restore. The exact graph for a build must be captured after
restoring with the pinned SDK (`global.json`):

```text
dotnet restore
dotnet list InventoryManagementSystem.sln package --include-transitive --format json > dependency-inventory.json
```

The resulting `dependency-inventory.json` is the evidence for the resolved
transitive package IDs, versions, and dependency paths for that build. Each
transitive package retains its own license and notice obligations; the
top-level MIT license is not a substitute. CI should retain this output with
the build evidence when a redistribution decision depends on the resolved
graph. The local audit for this change could not execute that command because
the pinned .NET SDK is not installed in the execution environment.

Some direct packages also carry non-NuGet redistribution material. The
QuestPDF 2026.7.1 package was inspected as a package archive and contains:

- `LICENSE.md`, containing the QuestPDF license-selection guide and license
  texts;
- `ExternalDependencyLicenses/`, including notices for qpdf, Skia, zlib,
  libpng, libjpeg-turbo, libwebp, HarfBuzz, Expat, Wuffs, and other native
  components; and
- `LatoFont/OFL.txt` plus Lato font files under the SIL Open Font License.

Those package-provided files should be preserved or made available in the
chosen redistribution channel. Whether the Community, Professional, or
Enterprise QuestPDF terms apply is owner-pending and depends on facts not
recorded in this repository.

## Frameworks and external runtime inputs

The projects also use the `Microsoft.AspNetCore.App` framework reference. The
container configuration uses Microsoft .NET base images and a PostgreSQL 16
image. These are runtime and deployment inputs rather than NuGet references;
their exact image digests and any separately distributed image content should
be recorded with the release artifact when an owner authorizes publication.
This document does not claim that the application source license covers those
images or the PostgreSQL distribution.

## Copied browser assets

These files are committed under
`InventoryManagementSystem.Web/wwwroot/lib/` and are separate from NuGet
restore:

| Asset | Version evidence | License / notice |
| --- | --- | --- |
| Bootstrap | 5.3.3 in the distributed CSS headers | MIT; notice at `wwwroot/lib/bootstrap/LICENSE` |
| jQuery | 3.7.1 in the distributed JS headers | MIT; notice at `wwwroot/lib/jquery/LICENSE.txt` |
| jQuery Validation | 1.21.0 in the distributed JS headers | MIT; notice at `wwwroot/lib/jquery-validation/LICENSE.md` |
| jQuery Validation Unobtrusive | Version is not embedded in the copied files | MIT notice at `wwwroot/lib/jquery-validation-unobtrusive/LICENSE.txt`; the source version should be confirmed before replacing or repackaging it |

The copied JavaScript, CSS, source maps, and fonts are covered by the notices
shipped beside each asset. Do not remove those files when preparing a
redistribution bundle.

## Owner review queue

The owner should record the outcome separately from this engineering change:

1. Confirm the intended Stockpile distribution and whether the project qualifies
   for the applicable QuestPDF terms; retain the QuestPDF package license and
   native dependency notices.
2. Confirm whether AutoMapper and MediatR use the RPL 1.5 terms or a Lucky Penny
   Software license for the intended distribution.
3. Confirm whether the FluentAssertions community terms are appropriate for the
   build/test use and whether test dependencies are excluded from the shipped
   artifact.
4. Capture a restored transitive graph for each shipped build and preserve the
   corresponding package notices. This record does not make legal conclusions
   for the owner.
