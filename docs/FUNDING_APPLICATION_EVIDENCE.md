---
layout: default
title: Funding application evidence index
description: Dated public evidence, unknowns, and owner verification for a funding draft.
---

# Funding application evidence index

**Evidence date:** 2026-09-18 (UTC). **Repository head:** `a21cb6ab2bedd6dd3d8968f9f11ab95d6179d177` on `master`.

This index supports the unsubmitted [Codex for Open Source application draft](FUNDING_APPLICATION_DRAFT.md). It distinguishes public repository evidence from private applicant eligibility, provider decisions, API billing, and real-world adoption. No application, account change, API call, provider contact, or external commitment was made for this draft.

## Program and form verification

The current [Codex for Open Source form](https://openai.com/form/codex-for-oss/) was opened and reviewed on 2026-09-18. The public page describes a program for maintainers of active open-source projects; it says reviewers consider repository usage, broad adoption or ecosystem importance, and active maintenance, and that applications are reviewed on a rolling basis. The rendered form asks for the applicant's name, ChatGPT-account email, public GitHub username/profile, public repository URL, primary/core-maintainer role, interest selection, OpenAI Organization ID, and narrative answers. It displays 500-character limits for the repository-qualification, API-credit-use, and “anything else” fields. The form was not submitted.

The linked [Program Terms](https://developers.openai.com/codex/codex-for-oss-terms), also checked 2026-09-18, say benefits may be limited, personal, non-transferable, and have no cash value; selection is discretionary and verification may be required. Applicants are told not to submit confidential information, and the terms do not promise confidentiality for application materials. The applicant must read the current terms and check local tax/legal conditions privately.

The separate [Codex Open Source Fund form](https://openai.com/form/codex-open-source-fund/) remains publicly reachable and still displays its earlier “up to $25,000 in API credits” copy. The relationship between that route and the current Codex for Open Source form is not established by these pages. This draft targets only the current `codex-for-oss` route; it does not claim that the older amount applies to it.

## Public repository snapshot

Values below came from public GitHub metadata on 2026-09-18 after `master` advanced to `a21cb6ab2bedd6dd3d8968f9f11ab95d6179d177`.

| Evidence | Observed | Qualification |
| --- | --- | --- |
| Repository | `nirzaf/merconiq`; public; default branch `master` | Repository metadata; does not identify the applicant. |
| License | MIT | `LICENSE` and GitHub repository metadata. |
| Stars / forks | 49 stars / 11 forks | Visibility signals only; not usage, adoption, customers, or impact. |
| Open issues | 76 | A backlog count, not evidence of demand or successful use. |
| GitHub releases | The public Releases API returned an empty list | No GitHub release record was observed at this snapshot; this is not a complete artifact-download measure. |
| Production users, downloads, deployments | Not recorded / not verified | Do not imply customers, active users, production operation, or broad adoption. |
| API-credit spend or attributable API cost | Not recorded | Do not estimate from ChatGPT subscription activity, native Codex reviews, or repository checks. |

The repository's [README at the evidence snapshot](https://github.com/nirzaf/merconiq/blob/a21cb6ab2bedd6dd3d8968f9f11ab95d6179d177/README.md) describes an open-source business-management platform evolving from inventory and procurement toward a modular ERP. The documented implementation includes the inventory/procurement foundation, company-scoped authorization, PostgreSQL-backed tests, and a staged ERP roadmap; this is not evidence that the complete ERPNext feature set is implemented. `ARCHITECTURE.md`, `ORGANIZATION_OWNERSHIP.md`, `INVENTORY_VALUATION.md`, and `USER_GUIDE.md` provide the corresponding source-level/product evidence.

Recent merged PRs [#476](https://github.com/nirzaf/merconiq/pull/476), [#477](https://github.com/nirzaf/merconiq/pull/477), [#478](https://github.com/nirzaf/merconiq/pull/478), and [#479](https://github.com/nirzaf/merconiq/pull/479) are examples of recent public maintenance and review activity. They are not external-contributor counts, adoption measures, user outcomes, or API-credit invoices. In particular, native Codex review integration is not API-credit expenditure.

The GitHub repository creation timestamp predates the Merconiq rebrand and is not used as Merconiq product tenure. [`PROVENANCE.md`](PROVENANCE.md) excludes simulated history from longevity, activity, usage, and impact claims; no generated history was used for this draft.

## API workload and cost worksheet

No project API-usage/cost baseline or owner-approved monthly workload is recorded. The blanks are intentional. Do not fill them with subscription usage, native GitHub Codex review events, CI minutes, or assumed token volumes.

| Workflow | Owner-approved runs/month | Representative attempts (include failures/retries) | API-billed usage and cost source | Measured cost per task |
| --- | --- | --- | --- | --- |
| Public issue triage | Not set | Not measured | Not recorded | Not recorded |
| Focused PR review | Not set | Not measured | Not recorded | Not recorded |
| PostgreSQL regression/migration test assistance | Not set | Not measured | Not recorded | Not recorded |
| Security, upgrade, and release documentation | Not set | Not measured | Not recorded | Not recorded |

After the owner authorizes a bounded pilot in an owner-controlled API organization, record for each attempt: date, model, task type, provider usage units/tokens, billed cost from the authoritative organization usage/billing record, success/failure, retries, test result, and human-review disposition. Use synthetic data and do not send customer or confidential data. Report aggregate measurements only after owner review.

Calculation to fill from measured values:

```text
Measured monthly base = sum(measured all-attempt cost per workflow × owner-approved monthly runs)
Contingency = owner-approved bounded amount or percentage (not set)
Requested amount = measured monthly base + approved contingency (not set)
```

If representative costs or run volumes remain unavailable, leave the calculation unfilled. Do not invent a credit request or represent free ChatGPT/Codex plan usage as API credits.

## Maintenance after support and public reporting

Reusable tests and documentation produced during any supported work should remain useful after the benefit period. API-funded workflows are optional maintenance aids, not runtime or deployment dependencies; stop API-funded runs when credits expire unless the owner separately approves another budget. A bounded public closeout could report aggregate task counts, all-attempt costs, failures/retries, and test/reviewer outcomes after owner review, without publishing applicant identities, prompts, customer data, or confidential information. This is a proposed commitment, not an owner-approved promise; confirm or revise it before applying.

## Owner completion gates

- Confirm which of the two visible OpenAI routes is intended and recheck its live fields, character limits, terms, and intake immediately before use.
- Privately verify the applicant's identity, account email, public profile, primary/core-maintainer role, OpenAI Organization ID, and any tax/location requirements.
- Select API credits and/or Codex Security only if the applicant wants those benefits and accepts the current terms. No Security request is inferred from existing scanners.
- Authorize a bounded API measurement pilot and budget, record failures/retries, and fill the worksheet from the API organization's billing/usage data.
- Review every answer and source, then separately authorize any form submission. This repository change does not submit, contact OpenAI, make a funding commitment, or claim selection.

## Reproducibility sources

- [Public repository metadata API](https://api.github.com/repos/nirzaf/merconiq).
- [Public open-issue filter (excludes pull requests)](https://github.com/nirzaf/merconiq/issues?q=is%3Aissue%20is%3Aopen).
- [Public releases API](https://api.github.com/repos/nirzaf/merconiq/releases).
- Program form and terms: links above; both reviewed 2026-09-18.
- Repository evidence policy: [`GITHUB_EVIDENCE.md`](GITHUB_EVIDENCE.md), [`CODEX_MAINTENANCE_EVIDENCE.md`](CODEX_MAINTENANCE_EVIDENCE.md), and [`PROVENANCE.md`](PROVENANCE.md).
