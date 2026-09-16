# Release candidate evidence

This document is a preparation record, not a published release. A candidate is
only considered ready when its commit, checks, install assumptions, smoke
coverage, and limitations are recorded. Creating a tag, GitHub Release, or
published artifact remains a separate owner-authorized action.

## Proposed candidate record

| Field | Value |
|---|---|
| Candidate label | `v<owner-approved-version>-rc.1` (not created) |
| Candidate commit | Record the full SHA after the preparation PR merges; current baseline is `b1646978f51ac7493d70e1b701685d0e8fd7b7f2` |
| Runtime | .NET SDK 10.0.300; PostgreSQL 16; Docker Linux containers for Compose/Testcontainers |
| Supported image architectures | `linux/amd64` and `linux/arm64` are configured; platform smoke evidence is required before calling either validated |
| Publication state | `release candidate prepared; publication pending` |

## Delivered capabilities to describe

The current repository contains inventory catalog, suppliers, locations, stock
receive/transfer/sale, transaction history, purchase orders, role-based
authentication, REST API endpoints, responsive UI, PDF/CSV reporting, anomaly
detection, and configurable forecasting. The default forecast implementation is
the managed moving average; ML.NET SSA is an explicit opt-in. Do not describe
planned, untested, or owner-only work as delivered capability.

## Required evidence before publication

- Full candidate SHA and `git diff` from the previous approved candidate.
- CI restore/build/test results, PostgreSQL integration result, coverage report,
  CodeQL, container scan, Trivy, and GitGuardian results linked to that SHA.
- Clean-install record using `docker compose config`, required non-empty secrets,
  migrations, administrator provisioning, login, item/location creation, receive,
  sell, transfer, and fresh-context balance/history checks with synthetic data.
- Restart/data-protection check and explicit local HTTP versus production
  reverse-proxy/TLS assumptions.
- `linux/amd64` and `linux/arm64` smoke results, or an honest statement that a
  platform is configured but not validated.
- Dependency and license review with unresolved terms listed rather than hidden
  by the top-level MIT license.
- Browser exclusions clearly separated from API-only tests.

The current known limitation is that a local smoke run is not recorded in this
preparation branch; remote CI is not a substitute for authenticated browser or
operator validation. Do not fill missing evidence with generated history,
invented metrics, or a green health endpoint.

The Docker and Release workflows validate the selected branch/tag against the
current remote ref and carry the exact full commit SHA through checkout,
metadata, and release notes. Docker publishes `sha-<full-commit>` as the
immutable image reference; Release verifies that image and the matching semver
image tag before creating a GitHub Release. Their manual `dry_run` inputs are
safe validation paths and do not log in, push, or create a release.

## Owner-authorized publication checklist

When separately authorized, the owner may create the approved tag and publish the
release. Until then, do not run these commands:

```bash
git tag v<owner-approved-version>-rc.1 <FULL_CANDIDATE_SHA>
git push origin v<owner-approved-version>-rc.1
```

After an authorized publication, verify the GitHub Release and each associated
Docker artifact by URL/digest, then append the actual evidence here. A merged
preparation PR alone never proves publication.
