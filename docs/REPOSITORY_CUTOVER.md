# Repository and artifact cutover guide

**Status:** Partial, repository-local guidance for issue [#267](https://github.com/nirzaf/merconiq/issues/267). This document records the selected identities and current repository configuration; it is not authorization to rename settings, publish artifacts, change DNS, or deploy.

**Verified against:** `nirzaf/merconiq` at `b18295deae288137b70de0c06fd694dfcbf034e4`, 2026-09-18. Live URL checks below were made at 2026-09-18 06:26 UTC. Recheck every live value before an owner-approved cutover.

## Canonical identities and current status

| Surface | Selected/canonical reference | Verified status |
|---|---|---|
| Source repository | <https://github.com/nirzaf/merconiq> | GitHub repository metadata reports this name, URL, and `master` as the default branch. |
| GitHub Pages documentation | <https://nirzaf.github.io/merconiq/> | Selected by the release owner and configured as the repository homepage, but **not reachable at verification time**: it returned HTTP 301 to `https://dotnetevangelist.net/merconiq/`, which returned HTTP 404. Do not describe this URL as a working live site until the owner-controlled Pages configuration is repaired and it is rechecked. |
| GHCR image name | `ghcr.io/nirzaf/merconiq` | Configured by the Docker and Release workflows. This source configuration alone does not prove that the package exists, is public, or has a currently pullable artifact. |

The Pages workflow in [`.github/workflows/pages.yml`](https://github.com/nirzaf/merconiq/blob/master/.github/workflows/pages.yml) uploads the repository's `docs/` directory using GitHub Actions Pages deployment. It contains no explicit Markdown/site-generator build step. The repository source therefore does not establish that `index.md` is rendered into a navigable `index.html` or that all page links and assets work in the deployed artifact. A successful workflow run proves that an artifact was deployed, not that the artifact is rendered correctly or the selected URL resolves: the current redirect is controlled outside this repository. This guide and the workflow do not change either condition.

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

This is a project site, so its path prefix is `/merconiq/`, not the domain root. The intended base URL is `https://nirzaf.github.io/merconiq/`. The repository workflow publishes files from `docs/`; it does not run a separate static-site generator or add custom URL rewriting.

- Keep links between documentation pages relative to the Markdown file, for example `[User Guide](USER_GUIDE.md)` from `docs/index.md`.
- Keep images, stylesheets, and other site assets inside `docs/` and reference them with a path relative to the page that uses them. Do not use root-relative paths such as `/assets/site.css`, which omit the `/merconiq/` project prefix.
- Configure and test the actual generated artifact with base URL `/merconiq/`; inspect that it contains a rendered entry point and that nested CSS, scripts, images, and navigation links resolve before treating Pages as usable.
- Until the owner repairs the external redirect, use the source documents in this repository (or their GitHub file views) as the reliable documentation access path. Do not claim the Pages URL works based solely on a green Pages workflow.

## GHCR image references and publication evidence

The current [`docker.yml`](https://github.com/nirzaf/merconiq/blob/master/.github/workflows/docker.yml) and [`release.yml`](https://github.com/nirzaf/merconiq/blob/master/.github/workflows/release.yml) configure `ghcr.io/nirzaf/merconiq`. The Docker workflow builds `linux/amd64` and `linux/arm64`, labels the image with its source repository and commit, and requests BuildKit provenance and SBOM attestations. For a successful authorized build it emits a commit-specific tag of the form `sha-<full-commit-sha>`; `master`/`latest` and semver aliases are mutable tag pointers, not immutable identities.

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

**Not yet evidenced by this guide:** GHCR package existence/visibility, anonymous or operator pull permissions, any current published tag/digest, platform runtime smoke tests, or registry-retention behavior. No image was built, pushed, tagged, or deleted for this documentation change.

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
| `https://dotnetevangelist.net/merconiq/` | Not a canonical documentation URL; the direct destination returned HTTP 404 during the check recorded above. | Its DNS, domain, user-site repository, and Pages configuration are outside this repository and untouched. |

Historical commits, tags, issues, package metadata, and documentation may still contain old product identifiers. Treat them as provenance, not as current installation instructions. Update maintained references as they are encountered; do not rewrite shared history to erase them.

## Owner-pending cutover gates

Issue #267 remains open. Repository-local documentation cannot complete these external or operational checks:

- Repair the inherited Pages redirect in the owner-controlled configuration, after assessing its effect on the user site and other Pages projects. Ensure the workflow artifact contains rendered HTML (including an entry point) and correct `/merconiq/`-prefixed assets; then request the selected URL and its nested CSS, scripts, images, and internal links. Require the canonical URL to return expected content rather than accepting a successful workflow alone.
- Verify GHCR package access with an actual pull and record the exact source SHA, workflow run, top-level digest, platform manifests, and provenance/SBOM subjects. Record any unsupported platform or missing attestation explicitly.
- Have the release owner decide and record the old package's consumer notice, retention/deprecation schedule, and whether any one-time migration aid is necessary. Do not assume or promise automatic package redirects.
- At each affected deployment, identify and preserve its actual PostgreSQL/Data Protection volumes and secrets; record a successful backup/restore or migration check before any operator changes project identity.

This pull request changes documentation only. It does not rename the repository, alter GitHub settings, change DNS/CNAME, modify registry packages, deploy Pages, publish images, or change the tracking status of issue #267.

## Source of current facts

- Repository metadata and homepage: <https://github.com/nirzaf/merconiq>.
- Pages deployment configuration: [`.github/workflows/pages.yml`](https://github.com/nirzaf/merconiq/blob/master/.github/workflows/pages.yml); owner-selected URL failure and authorization boundary: [issue #267](https://github.com/nirzaf/merconiq/issues/267).
- Container names, tags, source labels, architectures, and attestation settings: [`.github/workflows/docker.yml`](https://github.com/nirzaf/merconiq/blob/master/.github/workflows/docker.yml) and [`.github/workflows/release.yml`](https://github.com/nirzaf/merconiq/blob/master/.github/workflows/release.yml).
- Compose volume definitions: [`docker-compose.yml`](https://github.com/nirzaf/merconiq/blob/master/docker-compose.yml) and [`docker-compose.dev.yml`](https://github.com/nirzaf/merconiq/blob/master/docker-compose.dev.yml).
- Container evidence procedure: [`IMAGE_PROVENANCE.md`](IMAGE_PROVENANCE.md); data-protection and PostgreSQL recovery guidance: [`BACKUP_RESTORE.md`](BACKUP_RESTORE.md).
