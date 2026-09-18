# Repository and artifact cutover guide

**Status:** The release owner selected GitHub Pages as the canonical documentation host and authorized removing the `dotnetevangelist.net` mapping from GitHub Pages. On 2026-09-18, the Pages API for `nirzaf/nirzaf.github.io` was updated to `cname=null`; no `CNAME` file was present and DNS was not changed. This does not authorize a repository rename, legacy-package action, or deployment-volume change.

**Verified against:** `nirzaf/merconiq` master at `a72a90597f06ddc01fe03a29010158a23c889685`, 2026-09-18. Live URL checks below were made at 2026-09-18 09:51 UTC, after the profile-site custom-domain mapping was removed and before the Jekyll build was deployed.

## Canonical identities and current status

| Surface | Selected/canonical reference | Verified status |
|---|---|---|
| Source repository | <https://github.com/nirzaf/merconiq> | GitHub repository metadata reports this name, URL, and `master` as the default branch. |
| GitHub Pages documentation | <https://nirzaf.github.io/merconiq/> | Selected by the release owner and configured as the repository homepage. The profile-site Pages API now reports `cname=null` and `html_url=https://nirzaf.github.io/`; the Merconiq project-site API also reports `cname=null`. At 09:51 UTC the canonical URL returned HTTP 404 without an external-domain redirect because the deployed artifact had no rendered `index.html`. The updated Jekyll workflow in this change is not yet deployed; recheck the URL and nested assets after it runs on `master`. |
| GHCR image name | `ghcr.io/nirzaf/merconiq` | Configured by the Docker and Release workflows. This source configuration alone does not prove that the package exists, is public, or has a currently pullable artifact. |

The Pages workflow in [`.github/workflows/pages.yml`](https://github.com/nirzaf/merconiq/blob/master/.github/workflows/pages.yml) now builds the repository's `docs/` source with Jekyll, enables relative Markdown links, sets the `/merconiq` project base path, checks for rendered HTML and the theme stylesheet, then uploads `_site`. The profile-site custom-domain mapping was removed through the Pages API; the DNS zone was left unchanged. A green Pages run alone is not live-site proof, so verify the canonical URL, stylesheet, and navigation after deployment.

## Clone the canonical repository

Use the HTTPS URL for a credential-helper-managed clone:

```bash
git clone https://github.com/nirzaf/merconiq.git
cd merconiq
```

If SSH access is already configured for the account, the equivalent is:

```bash
git clone git@github.com:nirzaf/merconiq.git
cd merconiq
```

The checkout directory name is local and does not change the remote repository identity. However, Compose's default project name is derived from the project directory; see [operator volume mappings](#operator-volume-mappings-before-changing-a-checkout-or-project-name) before renaming an existing deployment directory.

## GitHub Pages base path and assets

This is a project site, so its path prefix is `/merconiq/`, not the domain root. The intended base URL is `https://nirzaf.github.io/merconiq/`. The Pages workflow builds Markdown in `docs/` into `_site` with Jekyll and uses the relative-links plugin for generated HTML destinations.

- Keep links between documentation pages relative to the Markdown file, for example `[User Guide](USER_GUIDE.md)` from `docs/index.md`; the Jekyll relative-links plugin converts them to generated HTML destinations.
- Keep images and other site assets inside `docs/`; use the Jekyll `relative_url` filter for root-based assets so the `/merconiq/` project prefix is included.
- The build config sets `baseurl: /merconiq`, and the workflow checks the generated entry point, User Guide page, theme stylesheet, and project-prefixed links before upload.
- After deployment, request the canonical URL and the stylesheet/guide links and verify they return usable content. A successful workflow alone is not public-site evidence.

## GHCR image references and publication evidence

The current [`docker.yml`](https://github.com/nirzaf/merconiq/blob/master/.github/workflows/docker.yml) and [`release.yml`](https://github.com/nirzaf/merconiq/blob/master/.github/workflows/release.yml) configure `ghcr.io/nirzaf/merconiq`. The Docker workflow builds `linux/amd64` and `linux/arm64`, labels the image with its source repository and commit, and requests BuildKit provenance and SBOM attestations. It constructs the immutable `sha-<full-candidate-sha>` tag directly from the resolver's `candidate_sha` output, including when a manual run's selected ref differs from the workflow revision. `master`/`latest` and semver aliases are mutable tag pointers, not immutable identities.

The repository's production [`docker-compose.yml`](https://github.com/nirzaf/merconiq/blob/master/docker-compose.yml) currently builds the app from the checked-out source; it does not select the GHCR image by default. A configured image name, successful build workflow, or tag string is not evidence that a package can be pulled. Confirm package visibility/access and a real manifest digest in the registry before using an image.

For an owner-approved published candidate, first copy the exact full candidate SHA and manifest digest from that candidate's successful Docker workflow summary. A commit-specific pull check is:

```bash
IMAGE=ghcr.io/nirzaf/merconiq
SOURCE_SHA=<verified-full-commit-sha>
docker pull "$IMAGE:sha-$SOURCE_SHA"
```

For a deployment or retained audit record, prefer the verified content digest rather than a tag:

```bash
IMAGE=ghcr.io/nirzaf/merconiq
MANIFEST_DIGEST=sha256:<verified-manifest-digest>
IMAGE_REF="$IMAGE@$MANIFEST_DIGEST"
docker buildx imagetools inspect "$IMAGE_REF"
```

Use [`IMAGE_PROVENANCE.md`](IMAGE_PROVENANCE.md) for read-only manifest and attestation inspection commands. Verify the inspected image's source, exact commit, digest, both expected platform manifests, and attestation subjects against the same workflow candidate. The configured BuildKit provenance/SBOM options do not by themselves prove signed SLSA provenance, reproducible builds, runtime health, or an independently authenticated publisher identity.

**Not yet evidenced by this guide:** GHCR package existence/visibility, anonymous or operator pull permissions, any current published tag/digest, platform runtime smoke tests, or registry-retention behavior. No artifact has been published by this unmerged pull request. Merging to `master` triggers the existing Docker workflow, whose publication remains gated by exact-candidate validation; no manual release or legacy image alias is added here.

## Operator volume mappings before changing a checkout or project name

The checked-in Compose files define these container paths and logical volume keys:

| Compose service | Logical volume / mount | Container destination | Purpose |
|---|---|---|---|
| `db` ([`docker-compose.yml`](https://github.com/nirzaf/merconiq/blob/master/docker-compose.yml)) | `pgdata` | `/var/lib/postgresql/data` | PostgreSQL database files. |
| `app` ([`docker-compose.yml`](https://github.com/nirzaf/merconiq/blob/master/docker-compose.yml)) | `dataprotection` | `/app/data` (keys are stored under `/app/data/keys`) | ASP.NET Core Data Protection key persistence. |
| `app` ([`docker-compose.dev.yml`](https://github.com/nirzaf/merconiq/blob/master/docker-compose.dev.yml)) | `./src/Merconiq.Web` | `/src/src/Merconiq.Web` (read-only) | Development source mount. |
| `app` ([`docker-compose.dev.yml`](https://github.com/nirzaf/merconiq/blob/master/docker-compose.dev.yml)) | `./src/Merconiq.Core` | `/src/src/Merconiq.Core` (read-only) | Development source mount. |
| `app` ([`docker-compose.dev.yml`](https://github.com/nirzaf/merconiq/blob/master/docker-compose.dev.yml)) | `./src/Merconiq.Infrastructure` | `/src/src/Merconiq.Infrastructure` (read-only) | Development source mount. |
| `app` ([`docker-compose.dev.yml`](https://github.com/nirzaf/merconiq/blob/master/docker-compose.dev.yml)) | `app-logs` | `/app/logs` | Development application logs. |

`pgdata`, `dataprotection`, and `app-logs` are Compose logical names. Unless an explicit project name or external volume is configured, Docker Compose prefixes named volumes with the Compose project name. That project name commonly defaults to the checkout directory name. A repository rename does not rename volume contents, but moving or renaming the local checkout can make Compose calculate a different project name and create new, empty volumes instead of selecting the existing ones.

Before changing an operator's checkout path or Compose project name:

1. Record the exact Compose project name and actual Docker volume names used by the running deployment; do not infer them from this repository name.
2. Back up PostgreSQL and the Data Protection keys following [`BACKUP_RESTORE.md`](BACKUP_RESTORE.md).
3. Keep the existing project name explicit with the operator's established Compose command/configuration, or deliberately map the existing named volumes. Verify the resolved configuration and volume attachments before starting services.
4. Do not use `docker compose down --volumes` / `-v`, `docker volume prune`, or a new project name as a migration strategy. A newly created empty volume can look like data loss even while the original volume remains present.

The table describes repository defaults, not a production inventory. Actual host paths, volume names, backup state, and operator-specific overrides must be confirmed at the deployment before cutover.

## Disposition of previous references

| Previous reference | Disposition for maintained instructions | What remains unknown / prohibited here |
|---|---|---|
| Stockpile / InventoryManagementSystem product and repository names, including the former `nirzaf/stockpile` source URL | Historical names remain valid in genuine repository history and rebrand records; use `nirzaf/merconiq` in new links, clone commands, labels, and release instructions. Do not rely on an old URL redirect without checking it. | No GitHub repository setting, redirect, issue history, namespace, or local operator checkout was changed by this guide. |
| `ghcr.io/nirzaf/inventorymanagementsystem` | Retired from current pull instructions. New maintained image references use the workflow-configured `ghcr.io/nirzaf/merconiq`. | The old package's existence, access, retention, consumers, and any deletion/deprecation action were not verified or changed. Do not delete it, publish an alias, or dual-publish without separate owner approval and an operator-impact review. |
| `https://dotnetevangelist.net/merconiq/` | Retired as a Merconiq documentation URL. The `nirzaf/nirzaf.github.io` Pages API mapping to `dotnetevangelist.net` was removed on 2026-09-18. | DNS was not changed. Other hosting or DNS behavior for the domain was not tested; this repository does not claim the domain itself was retired. |

Historical commits, tags, issues, package metadata, and documentation may still contain old product identifiers. Treat them as provenance, not as current installation instructions. Update maintained references as they are encountered; do not rewrite shared history to erase them.

## Owner-pending cutover gates

Issue #267 remains open. Repository-local documentation cannot complete these external or operational checks:

- After this Pages workflow is merged and runs, verify the canonical URL returns the rendered entry page, the generated stylesheet and User Guide load under `/merconiq/`, and internal navigation no longer leaves GitHub Pages. Record the final response codes and commit SHA; the current live endpoint has not yet passed these checks.
- Verify GHCR package access with an actual pull and record the exact source SHA, workflow run, top-level digest, platform manifests, and provenance/SBOM subjects. Record any unsupported platform or missing attestation explicitly.
- Have the release owner decide and record the old package's consumer notice, retention/deprecation schedule, and whether any one-time migration aid is necessary. Do not assume or promise automatic package redirects.
- At each affected deployment, identify and preserve its actual PostgreSQL/Data Protection volumes and secrets; record a successful backup/restore or migration check before any operator changes project identity.

This change builds and validates the rendered Pages artifact. The owner-authorized Pages API update removed the profile site's `dotnetevangelist.net` custom-domain mapping; DNS was not changed. It does not rename the repository, alter GHCR publication policy, manually publish a release, or add a legacy image alias. A normal merge to `master` triggers the existing Pages and Docker workflows; verify their results separately.

## Source of current facts

- Repository metadata and homepage: <https://github.com/nirzaf/merconiq>.
- Pages deployment configuration: [`.github/workflows/pages.yml`](https://github.com/nirzaf/merconiq/blob/master/.github/workflows/pages.yml); owner-selected URL failure and authorization boundary: [issue #267](https://github.com/nirzaf/merconiq/issues/267).
- Container names, tags, source labels, architectures, and attestation settings: [`.github/workflows/docker.yml`](https://github.com/nirzaf/merconiq/blob/master/.github/workflows/docker.yml) and [`.github/workflows/release.yml`](https://github.com/nirzaf/merconiq/blob/master/.github/workflows/release.yml).
- Compose volume definitions: [`docker-compose.yml`](https://github.com/nirzaf/merconiq/blob/master/docker-compose.yml) and [`docker-compose.dev.yml`](https://github.com/nirzaf/merconiq/blob/master/docker-compose.dev.yml).
- Container evidence procedure: [`IMAGE_PROVENANCE.md`](IMAGE_PROVENANCE.md); data-protection and PostgreSQL recovery guidance: [`BACKUP_RESTORE.md`](BACKUP_RESTORE.md).
