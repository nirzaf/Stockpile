# Merconiq readiness baseline

Reviewed 2026-09-16 against the live `nirzaf/merconiq` repository.

## Current repository state

| Evidence | Observed value |
| --- | --- |
| Default branch | `master` |
| Baseline commit | `b43b53252fa646df1d90f19021a088912d799967` |
| Repository | `https://github.com/nirzaf/merconiq` |
| Maintained projects | `Merconiq.Core`, `Merconiq.Infrastructure`, `Merconiq.Web`, `Merconiq.Tests` |
| Solution | `Merconiq.sln` |
| Open pull requests | #232, #234, #236, #239 |

The commit and pull-request values above are a dated snapshot. Refresh this
document before using it as release evidence; an issue or PR being open is not
evidence that its implementation is correct or approved.

## SPF backlog reconciliation

The original SPF backlog has one remaining owner-only item. The engineering
items below are closed in GitHub and retain their individual evidence and PR
history:

| Story | Tracking issue | Current disposition |
| --- | --- | --- |
| SPF-001 — contribution instructions | [#169](https://github.com/nirzaf/merconiq/issues/169) | Closed |
| SPF-002 — persisted inventory outcomes | [#170](https://github.com/nirzaf/merconiq/issues/170) | Closed |
| SPF-003 — duplicate-item API contract | [#171](https://github.com/nirzaf/merconiq/issues/171) | Closed; follow-up [#209](https://github.com/nirzaf/merconiq/issues/209) also closed |
| SPF-004 — purchase-order application tests | [#172](https://github.com/nirzaf/merconiq/issues/172) | Closed |
| SPF-005 — low-stock events | [#173](https://github.com/nirzaf/merconiq/issues/173) | Closed |
| SPF-006 — idempotency coordinator | [#174](https://github.com/nirzaf/merconiq/issues/174) | Closed; atomicity follow-up [#207](https://github.com/nirzaf/merconiq/issues/207) also closed |
| SPF-007 — repeated sale requests | [#175](https://github.com/nirzaf/merconiq/issues/175) | Closed |
| SPF-008 — repeated transfer requests | [#176](https://github.com/nirzaf/merconiq/issues/176) | Closed |
| SPF-009 — first-run setup | [#178](https://github.com/nirzaf/merconiq/issues/178) | Closed |
| SPF-010 — backup and restore | [#180](https://github.com/nirzaf/merconiq/issues/180) | Closed |
| SPF-011 — forecasting evidence | [#182](https://github.com/nirzaf/merconiq/issues/182) | Closed |
| SPF-012 — provenance | [#183](https://github.com/nirzaf/merconiq/issues/183) | Closed |
| SPF-013 — Codex maintenance evidence | [#184](https://github.com/nirzaf/merconiq/issues/184) | Closed |
| SPF-014 — release candidate | [#185](https://github.com/nirzaf/merconiq/issues/185) | Closed |
| SPF-015 — user and contributor validation | [#186](https://github.com/nirzaf/merconiq/issues/186) | Closed |
| SPF-016 — fund application | [#187](https://github.com/nirzaf/merconiq/issues/187) | Open; owner-authored evidence and submission authorization remain pending |

The current SFR follow-up inventory is:

- Closed engineering issues: [#213](https://github.com/nirzaf/merconiq/issues/213),
  [#214](https://github.com/nirzaf/merconiq/issues/214),
  [#215](https://github.com/nirzaf/merconiq/issues/215),
  [#216](https://github.com/nirzaf/merconiq/issues/216),
  [#217](https://github.com/nirzaf/merconiq/issues/217),
  [#218](https://github.com/nirzaf/merconiq/issues/218),
  [#219](https://github.com/nirzaf/merconiq/issues/219),
  [#220](https://github.com/nirzaf/merconiq/issues/220),
  [#222](https://github.com/nirzaf/merconiq/issues/222), and
  [#224](https://github.com/nirzaf/merconiq/issues/224).
- Open engineering/documentation issues with open PRs: [#221](https://github.com/nirzaf/merconiq/issues/221),
  [#223](https://github.com/nirzaf/merconiq/issues/223),
  [#225](https://github.com/nirzaf/merconiq/issues/225), and
  [#226](https://github.com/nirzaf/merconiq/issues/226).

## Current-source checks

The current source contains explicit evidence for the previously identified
readiness controls:

- Administrator bootstrap: `src/Merconiq.Web/Configuration/AdminBootstrapCommand.cs`,
  `src/Merconiq.Infrastructure/Data/AdminBootstrapService.cs`, and the bootstrap
  integration tests.
- Compose separation: `docker-compose.yml`, `docker-compose.dev.yml`, and
  `scripts/validate-compose.sh`.
- Cancellation and idempotency: `src/Merconiq.Core/Services/StockService.cs`,
  `src/Merconiq.Web/Services/IdempotencyKeyStore.cs`, and their tests.
- Cookie security-stamp validation: `src/Merconiq.Web/Configuration/IdentityExtensions.cs`
  and `tests/Merconiq.Tests/Integration/TenantAuthenticationTests.cs`.

These source references show where the controls are implemented; they do not
claim that a local PostgreSQL, Docker, production, or provider run was
executed. The current CI checks and any missing local runs remain separate
evidence records.

## Overlap and dependency map

- #264 is the reconciliation prerequisite for the M00 identity work.
- #265 (rename) must follow #264 and precede #266 (layout move); those changes
  must not be combined because path/namespace changes would obscure baseline
  verification.
- #267 is an owner-controlled repository/image/documentation cutover and is not
  an automated engineering task.
- The open SFR documentation/workflow PRs are independent of the M00 rename
  chain and can be reviewed in parallel, but their current heads must be
  revalidated after each base-branch change.

