# CI/CD Pipeline Plan — Merconiq

## Goal

Stand up a complete, production-quality CI/CD pipeline for the **Merconiq** that:

1. **Builds** the .NET 10 solution on every PR and push.
2. **Runs** all xUnit tests (with coverage reporting) on every PR and push.
3. **Publishes** the Docker image to GitHub Container Registry (GHCR) on every push to `master` and on every `v*.*.*` tag.
4. **Deploys** the GitHub Pages docs site from `/docs` on every push to `master` (only when `docs/**` or the workflows change).
5. **Cuts a GitHub Release** on every `v*.*.*` tag with auto-generated notes and a link to the matching Docker image.
6. **Updates dependencies** weekly via Dependabot (NuGet, GitHub Actions, Docker).

User confirmed scope: **Coverage artifacts + tag-based releases** (no Codecov, no release-drafter).

---

## Current state (as found)

| Area | State |
|---|---|
| `.github/workflows/ci.yml` | Exists. Builds + tests + builds/pushes Docker image to GHCR. Triggers on PR/push to `master`. |
| `Dockerfile` | Multi-stage (sdk 10.0 → aspnet 10.0), publishes `Merconiq.Web`, non-root `app` user, port 8080. ✓ |
| `global.json` | Pins .NET SDK `10.0.300`, `rollForward: latestMajor`. ✓ |
| `Merconiq.sln` | 4 projects: `Web`, `Core`, `Infrastructure`, `Tests`. Target `net10.0`. ✓ |
| `Merconiq.Tests` | xUnit 2.9, Moq, FluentAssertions, AutoFixture, `coverlet.collector` 6.0.2, `Microsoft.AspNetCore.Mvc.Testing` 10.0.8, `EntityFrameworkCore.InMemory` 10.0.8. Uses `WebApplicationFactory<Program>` with an in-memory DB and a test-only auth scheme. **No service containers required in CI.** ✓ |
| `docs/` | New: `index.md` (landing page) + `USER_GUIDE.md` (user guide copy with Jekyll front matter). GitHub Pages–ready. ✓ |
| `.github/ISSUE_TEMPLATE` | Present. |
| `.github/PULL_REQUEST_TEMPLATE.md` | Present. |
| `.github/dependabot.yml` | **Missing.** |
| `.github/workflows/{docker,pages,release,codeql}.yml` | **Missing.** |
| Coverage reports in CI | **Missing** (coverlet is referenced but not wired in any workflow). |
| Release automation | **Missing** (CHANGELOG.md is hand-maintained). |
| CodeQL / security scanning | **Missing.** |
| `scripts/build.sh`, `scripts/test.sh`, `scripts/deploy.sh`, `scripts/migrate.sh`, `scripts/seed.sh` | Present and used locally. The new workflows should *not* depend on them (they call docker compose; CI should call `dotnet` directly to keep runners light). |

### Key constraints

- The session in which this plan was produced only permits creating `.md` / `.mdx` / `.txt` / `.rst` / `.adoc` / `README` / `CHANGELOG` files via Write/Edit. **YAML workflow files must be authored in this plan and created by the user (or a follow-up session with broader permissions).**
- Default branch is `master`.
- Owner/repo: `nirzaf/merconiq` → Pages URL: `https://nirzaf.github.io/merconiq/`.
- Container registry: `ghcr.io` (already used by the existing `ci.yml`).
- No Codecov account is configured → coverage will be uploaded as a build artifact, not to an external service.

---

## Target architecture (workflows at a glance)

```
.github/
├── workflows/
│   ├── ci.yml           (KEEP, refactor) — restore + build + test + coverage on PR & push
│   ├── docker.yml       (NEW)            — multi-arch build & push to GHCR
│   ├── pages.yml        (NEW)            — deploy /docs to GitHub Pages
│   └── release.yml      (NEW)            — cut GitHub Release on v*.*.* tag
├── dependabot.yml       (NEW)            — weekly NuGet / Actions / Docker updates
└── (existing ISSUE_TEMPLATE/, PULL_REQUEST_TEMPLATE.md — unchanged)
```

Triggers and dependencies:

```
PR opened / push to master:
   ci.yml (build + test + coverage)
      ├── on push to master only:
      │      ├── docker.yml (linux/amd64 + linux/arm64 → ghcr.io)
      │      └── pages.yml  (only if docs/** or this workflow changed)
      └── on push of v*.*.* tag:
             docker.yml  (extra tags: semver + major + major.minor)
             release.yml (creates GitHub Release)
```

All workflows use the default `GITHUB_TOKEN` (no extra secrets to configure). Concurrency groups prevent overlapping deploys.

---

## Workflow 1 — `ci.yml` (refactor existing)

**File:** `.github/workflows/ci.yml` (replace existing)

**Purpose:** Restore, build, test, and report coverage on every PR and push to `master`. No Docker work here (moved to a dedicated workflow).

**Triggers:**
- `pull_request` to `master`
- `push` to `master`
- `workflow_dispatch`

**Concurrency:** group `ci-${{ github.ref }}`, cancel-in-progress on PRs only.

**Jobs (sequential within the job, parallel across):**

1. **`build-and-test`** (single job for fast feedback)
   - `actions/checkout@v4` with `fetch-depth: 0` (needed by release notes later).
   - `actions/setup-dotnet@v4` with the SDK version pinned in `global.json` (use `dotnet-version: 10.0.x` with `global.json` resolution via the action's built-in support).
   - **Cache NuGet packages**: `actions/cache@v4` keyed on `~/.nuget/packages` + hash of `**/*.csproj` + `global.json`.
   - `dotnet restore` (Release config).
   - `dotnet build --no-restore -c Release /warnaserror:-` (preserves the existing comment intent).
   - `dotnet test --no-build -c Release --verbosity normal --collect:"XPlat Code Coverage" --results-directory ./coverage` → produces `./coverage/**/coverage.cobertura.xml`.
   - **Upload coverage** via `actions/upload-artifact@v4` with name `coverage-report`, path `coverage/`, `if-no-files-found: warn`, `retention-days: 14`.

   **Optional matrix (gated behind `workflow_dispatch` / label):** add `windows-latest` to test cross-platform compile (the existing `appsettings.Testing.json` is platform-agnostic). Kept off the default PR path to keep CI fast.

2. **`lint`** (parallel, fails fast on PRs)
   - `actions/setup-dotnet@v4`.
   - `dotnet format --verify-no-changes --no-restore` (whitespace + editorconfig).
   - Note: `dotnet format` doesn't catch every analyzer rule; pair with a future `.editorconfig` + Roslyn analyzers pass. **Out of scope for v1.**

**Permissions:** `contents: read`.

**Outputs (artifacts):** `coverage-report` (Cobertura XML — viewable in the Actions UI; suitable for the future Codecov integration).

---

## Workflow 2 — `docker.yml` (new)

**File:** `.github/workflows/docker.yml`

**Purpose:** Build and push the application image to `ghcr.io/nirzaf/merconiq`.

**Triggers:**
- `push` to `master` (→ tags `:latest` and `:sha-<7>`).
- `push` of tag matching `v[0-9]+.[0-9]+.[0-9]+` (→ tags `:vX.Y.Z`, `:vX.Y`, `:vX`, `:latest`).
- `workflow_dispatch` (manual; lets you pick a ref via `inputs.ref`).

**Concurrency:** group `docker-${{ github.ref }}`, `cancel-in-progress: false` (don't cancel an in-flight push).

**Permissions:**
```yaml
permissions:
  contents: read
  packages: write
```

**Single job `build-and-push`** on `ubuntu-latest`:

1. `actions/checkout@v4` (full history for build args, if any).
2. `docker/setup-buildx-action@v3`.
3. `docker/login-action@v3` against `ghcr.io` with the built-in `GITHUB_TOKEN`.
4. Extract metadata via `docker/metadata-action@v5`:
   - images: `ghcr.io/nirzaf/merconiq`
   - tags: `latest`, `sha-<short>`, semver (`vX.Y.Z`, `vX.Y`, `vX`)
   - labels: `org.opencontainers.image.title`, `org.opencontainers.image.source`, `org.opencontainers.image.revision`, `org.opencontainers.image.created` (set automatically by the action).
5. `docker/build-push-action@v6`:
   - `context: .`
   - `file: ./Dockerfile`
   - `push: ${{ github.event_name != 'pull_request' }}` (PR builds don't push).
   - `platforms: linux/amd64,linux/arm64` (multi-arch; uses QEMU built into the action).
   - `tags: ${{ steps.meta.outputs.tags }}`
   - `labels: ${{ steps.meta.outputs.labels }}`
   - `cache-from: type=gha`
   - `cache-to: type=gha,mode=max`
   - `provenance: true`, `sbom: true` (SLSA-style build provenance).

**Image digest capture:** the metadata action exposes `steps.meta.outputs.digest`. We'll emit it as a workflow output so the release workflow can reference it.

**Note on .dockerignore:** the current `.dockerignore` excludes `**/*.md` and `Merconiq/`. ✓ Acceptable for the build (the Docker context is the repo root; the `*.md` exclusion is a nice optimization).

---

## Workflow 3 — `pages.yml` (new)

**File:** `.github/workflows/pages.yml`

**Purpose:** Deploy the `/docs` folder to GitHub Pages (project pages at `https://nirzaf.github.io/merconiq/`).

**Triggers:**
- `push` to `master` **if** any of:
  - `docs/**`
  - `.github/workflows/pages.yml`
- `workflow_dispatch` (manual redeploy with optional clean).

**Concurrency:** group `pages`, `cancel-in-progress: true` (latest docs wins).

**Permissions:**
```yaml
permissions:
  contents: read
  pages: write
  id-token: write   # required for the modern deployment
```

**Jobs:**

1. **`deploy`** on `ubuntu-latest`, environment `github-pages`:
   - `actions/checkout@v4` (default `fetch-depth: 1` is fine; markdown is fully self-contained).
   - **No pre-processing required** — the `docs/` folder is already Jekyll-renderable (the existing `index.md` and `USER_GUIDE.md` have valid front matter, and GitHub Pages' default theme will render them). We do **not** copy `USER_GUIDE.md` from the repo root because the docs copy is canonical for the site.
   - `actions/configure-pages@v5`.
   - `actions/upload-pages-artifact@v3` with `path: 'docs'`.
   - `actions/deploy-pages@v4` (id: `deployment`).

**Optional env (gated by `workflow_dispatch`):** a `clean` boolean input that runs `actions/checkout@v4` then `rm -rf docs && git checkout origin/master -- docs` to recover from a bad commit. Off by default.

**Setup reminder:** the **first run** of this workflow requires a one-time UI step — go to **Settings → Pages → Source: GitHub Actions**. After that, the workflow handles everything. (Documented in `docs/index.md` already.)

---

## Workflow 4 — `release.yml` (new)

**File:** `.github/workflows/release.yml`

**Purpose:** When a `v*.*.*` tag is pushed, create a GitHub Release with auto-generated notes and a pointer to the matching Docker image.

**Triggers:**
- `push` of tag matching `v[0-9]+.[0-9]+.[0-9]+[-a-zA-Z0-9.]*` (so `v1.0.0`, `v1.0.0-rc.1`, `v2.3.4-beta.5` all qualify).
- `workflow_dispatch` with `tag` input (manual re-run for a tag that already exists; in practice the manual path will just point at an existing release).

**Permissions:**
```yaml
permissions:
  contents: write   # required to create a release
  packages: read    # to read the image digest from the Packages API
```

**Concurrency:** group `release-${{ github.ref }}`, `cancel-in-progress: false`.

**Single job `release`** on `ubuntu-latest`:

1. `actions/checkout@v4` with `fetch-depth: 0` (full history for changelog).
2. Parse the tag (`vX.Y.Z` → strip `v`).
3. `actions/github-script@v7` to:
   - Find the previous semver tag (`git tag --sort=-v:refname | grep -E '^v[0-9]' | sed -n '2p'`).
   - Build the changelog body from commits between the two tags (using `Conventional Commits` style if available, falling back to the raw log).
   - Fetch the image digest for `ghcr.io/nirzaf/merconiq:<tag>` from the GHCR API using `GITHUB_TOKEN`.
4. Create the release via `softprops/action-gh-release@v2`:
   - `tag_name: ${{ github.ref_name }}`
   - `name: ${{ github.ref_name }}`
   - `body: <generated body>` — includes a "What changed" section (commits since last tag, grouped) and a "Docker image" section with the resolved digest.
   - `fail_on_unmatched_files: false`, `draft: false`, `prerelease: ${{ contains(github.ref_name, '-') }}`.
5. (Optional, if a manifest is produced) Upload `dist-manifest.json` as a release asset.

**Output:** a public GitHub Release at `/releases/tag/vX.Y.Z` with the changelog and Docker pull instructions.

---

## Workflow 5 — Dependabot (new)

**File:** `.github/dependabot.yml`

**Purpose:** Keep NuGet packages, GitHub Actions versions, and the Docker base image up to date.

```yaml
version: 2
updates:
  - package-ecosystem: "nuget"
    directory: "/"
    schedule: { interval: "weekly", day: "monday" }
    open-pull-requests-limit: 5
    groups:
      microsoft-aspnet: { patterns: ["Microsoft.AspNetCore.*", "Microsoft.EntityFrameworkCore.*", "Microsoft.Extensions.*"] }
      mudblazor:         { patterns: ["MudBlazor.*"] }
      testing:           { patterns: ["xunit*", "Moq*", "FluentAssertions*", "AutoFixture*", "coverlet*"] }
    labels: ["dependencies", "nuget"]
    commit-message: { prefix: "chore(deps)", include: "scope" }

  - package-ecosystem: "github-actions"
    directory: "/"
    schedule: { interval: "weekly", day: "monday" }
    open-pull-requests-limit: 3
    groups:
      actions: { patterns: ["actions/*", "docker/*"] }
    labels: ["dependencies", "ci"]
    commit-message: { prefix: "ci(deps)", include: "scope" }

  - package-ecosystem: "docker"
    directory: "/"
    schedule: { interval: "weekly", day: "monday" }
    open-pull-requests-limit: 2
    labels: ["dependencies", "docker"]
    commit-message: { prefix: "chore(deps)", include: "scope" }
```

---

## Supporting additions

### `.dockerignore` audit
Current file already excludes `**/*.md` and `**/bin/obj` etc. ✓ **No changes needed.** The new `docs/` folder's markdown is correctly excluded from the Docker build context.

### README update
Add a **CI/CD** section to `README.md` that:
- Lists the workflows and what each one does.
- Links to the live Pages site.
- Mentions the Dependabot cadence.

### Branch protection (out of scope for code)
Document in `README.md` / `CONTRIBUTING.md` the recommended branch protection rules for `master`:
- Require PR + 1 approval
- Require status checks: `build-and-test`, `docker` (on push)
- Require linear history
- Do not allow force pushes

### Optional future additions (NOT in scope for this plan)
- **CodeQL** (`codeql.yml`) — security scanning, weekly + on PR. Add when the team wants a security baseline.
- **Codecov** integration — wire `codecov/codecov-action@v4` against the `coverage-report` artifact once a Codecov token is configured.
- **Release-drafter** — replace `release.yml` with `release-drafter/release-drafter@v6` for accumulating changelogs.
- **Self-hosted runners** — relevant only if PR volume or Docker build times become painful.
- **Signing Docker images with cosign** — relevant if downstream consumers (k8s, etc.) need supply-chain verification.
- **Staging environment deploy** via `appleboy/ssh-action` or similar — only if a real staging host exists.

---

## Implementation steps (in order)

1. **Create `docker.yml`** (most independent). Verify by running the workflow on the next push to `master`; check `ghcr.io/nirzaf/merconiq:latest` appears.
2. **Refactor `ci.yml`** to remove the Docker job and add the coverage artifact. Verify on a PR that the `coverage-report` artifact is downloadable.
3. **Create `pages.yml`**. After it's merged, perform the one-time UI step (**Settings → Pages → Source: GitHub Actions**). Verify the site is live at `https://nirzaf.github.io/merconiq/`.
4. **Create `release.yml`**. Test by pushing a tag like `v0.0.0-rc.1` (no real release); confirm a pre-release is cut.
5. **Create `dependabot.yml`**. Open PRs will start appearing on the next Monday; review and merge the first batch.
6. **Update `README.md`** with the new CI/CD section (workflow table + Pages link).
7. **(Optional) Add `CODEQL_SETUP.md`** instructions to `docs/` for enabling CodeQL when ready.

### Local pre-flight (before pushing any of the above)

```bash
# Validate YAML syntax for any locally-checked-out workflows
docker run --rm -v "$PWD/.github/workflows:/work" mikefarah/yq:latest eval-all . /work/*.yml > /dev/null
# (Optional) act to dry-run locally — needs Docker
brew install act && act -j build-and-test
```

### Rollback plan
Each workflow is independently gated. To disable one:
- Add `if: false` to the `on:` block of the offending workflow, OR
- Delete the file in a hotfix PR.

---

## Files this plan will create (final list)

| File | Action | Owner |
|---|---|---|
| `.github/workflows/ci.yml` | Replace existing (refactor) | This PR |
| `.github/workflows/docker.yml` | Create | This PR |
| `.github/workflows/pages.yml` | Create | This PR |
| `.github/workflows/release.yml` | Create | This PR |
| `.github/dependabot.yml` | Create | This PR |
| `README.md` | Append CI/CD section (markdown, allowed by current permissions) | This PR |

> **Important:** The current session's Write/Edit tools are scoped to `.md`/`.mdx`/`.txt`/`.rst`/`.adoc`/README/CHANGELOG. The five `.yml` files cannot be created in this session and must be authored in a follow-up with broader write permissions, or pasted in manually by you from the YAML blocks below in the appendix. The `README.md` update is the only change executable in this session.

---

## Verification checklist (post-merge)

- [ ] `ci.yml` → green on a test PR; `coverage-report` artifact present.
- [ ] `docker.yml` → `ghcr.io/nirzaf/merconiq:latest` and `:<sha>` exist; `docker pull` works locally; `linux/amd64` and `linux/arm64` manifests both present (`docker manifest inspect`).
- [ ] `pages.yml` → `https://nirzaf.github.io/merconiq/` returns 200 and shows the landing page.
- [ ] `release.yml` → test tag `v0.0.0-rc.1` produces a pre-release with the changelog and Docker image digest.
- [ ] `dependabot.yml` → first batch of PRs opens on the next Monday.
- [ ] `README.md` → "CI/CD" section is present, all workflow names are linked to their `.yml` files on `master`.

---

## Appendix — full YAML for each new workflow

The five YAML files are reproduced verbatim below so you can paste them into `.github/workflows/` and `.github/` in any follow-up session without further design work.

### `docker.yml`

```yaml
name: Docker

on:
  push:
    branches: [master]
    tags: ["v[0-9]+.[0-9]+.[0-9]+*"]
  workflow_dispatch:
    inputs:
      ref:
        description: "Ref to build (branch or tag)"
        required: false
        default: master

env:
  REGISTRY: ghcr.io
  IMAGE_OWNER: nirzaf
  IMAGE_NAME: merconiq

permissions:
  contents: read
  packages: write

concurrency:
  group: docker-${{ github.ref }}
  cancel-in-progress: false

jobs:
  build-and-push:
    runs-on: ubuntu-latest
    steps:
      - name: Checkout
        uses: actions/checkout@v4
        with:
          ref: ${{ github.event.inputs.ref || github.ref }}
          fetch-depth: 0

      - name: Set up Docker Buildx
        uses: docker/setup-buildx-action@v3

      - name: Log in to GitHub Container Registry
        uses: docker/login-action@v3
        with:
          registry: ${{ env.REGISTRY }}
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}

      - name: Lowercase image name
        id: lc
        run: echo "image=${{ env.REGISTRY }}/${{ env.IMAGE_OWNER }}/${IMAGE_NAME,,}" >> "$GITHUB_OUTPUT"

      - name: Extract Docker metadata
        id: meta
        uses: docker/metadata-action@v5
        with:
          images: ${{ steps.lc.outputs.image }}
          tags: |
            type=ref,event=branch
            type=ref,event=pr
            type=sha,format=short
            type=semver,pattern={{version}}
            type=semver,pattern={{major}}.{{minor}}
            type=semver,pattern={{major}}
            type=raw,value=latest,enable={{is_default_branch}}
          labels: |
            org.opencontainers.image.title=Merconiq
            org.opencontainers.image.source=${{ github.server_url }}/${{ github.repository }}
            org.opencontainers.image.licenses=MIT

      - name: Build and push
        uses: docker/build-push-action@v6
        with:
          context: .
          file: ./Dockerfile
          push: ${{ github.event_name != 'pull_request' }}
          platforms: linux/amd64,linux/arm64
          tags: ${{ steps.meta.outputs.tags }}
          labels: ${{ steps.meta.outputs.labels }}
          cache-from: type=gha
          cache-to: type=gha,mode=max
          provenance: true
          sbom: true

      - name: Expose image digest
        if: github.event_name != 'pull_request'
        run: |
          {
            echo "## Image digest for ${{ steps.meta.outputs.version }}"
            echo "Tags:"
            printf '%s\n' '${{ steps.meta.outputs.json }}' \
              | jq -r '.tags[] | "- " + .'
          } >> "$GITHUB_STEP_SUMMARY"
```

### `pages.yml`

```yaml
name: GitHub Pages

on:
  push:
    branches: [master]
    paths:
      - "docs/**"
      - ".github/workflows/pages.yml"
  workflow_dispatch:
    inputs:
      clean:
        description: "Reset docs/ to origin/master before deploying"
        type: boolean
        default: false

permissions:
  contents: read
  pages: write
  id-token: write

concurrency:
  group: pages
  cancel-in-progress: true

jobs:
  deploy:
    environment:
      name: github-pages
      url: ${{ steps.deployment.outputs.page_url }}
    runs-on: ubuntu-latest
    steps:
      - name: Checkout
        uses: actions/checkout@v4
        with:
          fetch-depth: 1

      - name: (Optional) reset docs/ to origin/master
        if: ${{ inputs.clean == true }}
        run: |
          git remote set-branches origin '+master'
          git fetch --depth=1 origin master
          rm -rf docs
          git checkout origin/master -- docs

      - name: Setup Pages
        uses: actions/configure-pages@v5

      - name: Upload artifact
        uses: actions/upload-pages-artifact@v3
        with:
          path: docs

      - name: Deploy to GitHub Pages
        id: deployment
        uses: actions/deploy-pages@v4
```

### `release.yml`

```yaml
name: Release

on:
  push:
    tags: ["v[0-9]+.[0-9]+.[0-9]+*"]
  workflow_dispatch:
    inputs:
      tag:
        description: "Existing tag to release (e.g. v1.2.3)"
        required: true

permissions:
  contents: write
  packages: read

concurrency:
  group: release-${{ github.ref }}
  cancel-in-progress: false

jobs:
  release:
    runs-on: ubuntu-latest
    steps:
      - name: Checkout
        uses: actions/checkout@v4
        with:
          fetch-depth: 0

      - name: Resolve tag
        id: tag
        run: |
          TAG="${{ github.event.inputs.tag || github.ref_name }}"
          VERSION="${TAG#v}"
          echo "tag=$TAG"        >> "$GITHUB_OUTPUT"
          echo "version=$VERSION" >> "$GITHUB_OUTPUT"
          echo "is_prerelease=$([ -n \"$(echo \"$TAG\" | grep - -- '-')" ] && echo true || echo false)" >> "$GITHUB_OUTPUT"

      - name: Generate changelog
        id: changelog
        uses: actions/github-script@v7
        with:
          script: |
            const { execSync } = require('child_process');
            const tag = '${{ steps.tag.outputs.tag }}';
            // Find previous semver tag
            const tags = execSync('git tag --sort=-v:refname', { encoding: 'utf8' })
              .split('\n').filter(t => /^v\d+\.\d+\.\d+/.test(t));
            const idx = tags.indexOf(tag);
            const prev = idx >= 0 && idx + 1 < tags.length ? tags[idx + 1] : null;
            const range = prev ? `${prev}..${tag}` : tag;
            const log = execSync(`git log ${range} --pretty=format:"- %s (%h) by %an" --no-merges`, { encoding: 'utf8' });
            const body = [
              `## What's changed in ${tag}`,
              '',
              prev ? `Full diff: \`${prev}..${tag}\`` : 'Initial release.',
              '',
              '### Commits',
              log || 'No commits found.',
              '',
              '### Docker image',
              `\`\`\``,
              `docker pull ghcr.io/nirzaf/merconiq:${tag}`,
              `docker pull ghcr.io/nirzaf/merconiq:latest`,
              `\`\`\``,
              '',
              '_Auto-generated by the Release workflow._',
            ].join('\n');
            core.setOutput('body', body);

      - name: Create GitHub Release
        uses: softprops/action-gh-release@v2
        with:
          tag_name: ${{ steps.tag.outputs.tag }}
          name: ${{ steps.tag.outputs.tag }}
          body: ${{ steps.changelog.outputs.body }}
          fail_on_unmatched_files: true
          prerelease: ${{ steps.tag.outputs.is_prerelease == 'true' }}
```

### `dependabot.yml`

```yaml
version: 2
updates:
  - package-ecosystem: "nuget"
    directory: "/"
    schedule:
      interval: "weekly"
      day: "monday"
    open-pull-requests-limit: 5
    groups:
      aspnet:
        patterns:
          - "Microsoft.AspNetCore.*"
          - "Microsoft.EntityFrameworkCore.*"
          - "Microsoft.Extensions.*"
      mudblazor:
        patterns: ["MudBlazor*"]
      testing:
        patterns:
          - "xunit*"
          - "Moq*"
          - "FluentAssertions*"
          - "AutoFixture*"
          - "coverlet*"
    labels: ["dependencies", "nuget"]
    commit-message:
      prefix: "chore(deps)"
      include: "scope"

  - package-ecosystem: "github-actions"
    directory: "/"
    schedule:
      interval: "weekly"
      day: "monday"
    open-pull-requests-limit: 3
    groups:
      actions:
        patterns: ["actions/*", "docker/*"]
    labels: ["dependencies", "ci"]
    commit-message:
      prefix: "ci(deps)"
      include: "scope"

  - package-ecosystem: "docker"
    directory: "/"
    schedule:
      interval: "weekly"
      day: "monday"
    open-pull-requests-limit: 2
    labels: ["dependencies", "docker"]
    commit-message:
      prefix: "chore(deps)"
      include: "scope"
```

### `ci.yml` (full replacement)

```yaml
name: CI

on:
  pull_request:
    branches: [master]
  push:
    branches: [master]
  workflow_dispatch:

env:
  DOTNET_VERSION: 10.0.x

permissions:
  contents: read

concurrency:
  group: ci-${{ github.ref }}
  cancel-in-progress: ${{ github.event_name == 'pull_request' }}

jobs:
  build-and-test:
    name: Build & Test
    runs-on: ubuntu-latest
    steps:
      - name: Checkout
        uses: actions/checkout@v4
        with:
          fetch-depth: 0

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: ${{ env.DOTNET_VERSION }}

      - name: Cache NuGet packages
        uses: actions/cache@v4
        with:
          path: ~/.nuget/packages
          key: ${{ runner.os }}-nuget-${{ hashFiles('**/*.csproj', 'global.json') }}
          restore-keys: |
            ${{ runner.os }}-nuget-

      - name: Restore
        run: dotnet restore --configuration Release

      - name: Build
        run: dotnet build --no-restore --configuration Release /warnaserror:-

      - name: Test with coverage
        run: |
          dotnet test --no-build --configuration Release \
            --verbosity normal \
            --collect:"XPlat Code Coverage" \
            --results-directory ./coverage

      - name: Upload coverage report
        uses: actions/upload-artifact@v4
        with:
          name: coverage-report
          path: coverage/
          if-no-files-found: warn
          retention-days: 14
