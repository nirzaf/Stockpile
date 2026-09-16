# Codex maintenance evidence

This file is a traceable template for maintenance work. Complete it from live
GitHub and command output; use `not recorded` or `unknown` when a measurement was
not collected. Native Codex review is a repository review signal, not automatic
evidence of Stockpile API-credit spending.

## Entry template

```markdown
### [YYYY-MM-DD] Issue #<number> — <short title>

- Task type: <bug | test | documentation | security | release preparation>
- Issue: https://github.com/nirzaf/stockpile/issues/<number>
- PR: https://github.com/nirzaf/stockpile/pull/<number>
- Branch: `<branch>`
- Tested commit: `<full SHA>`
- Reviewed commit: `<full SHA>`
- Merge commit: `<full SHA or not merged>`
- Commands actually run: `<commands, with environment notes>`
- Test results: `<pass/fail, links or output>`
- Failures encountered: `<none, or exact failure and link>`
- Fixes applied: `<short factual summary>`
- Native Codex review: `<completed/no findings, or findings and links>`
- Other review findings: `<accepted/rejected disposition with evidence>`
- Human disposition: `<who approved, what remained pending, or not recorded>`
- Usage/cost: `not recorded` unless measured from an authoritative source
- Human review time: `not recorded` unless measured
- Limitations and owner follow-up: `<explicit remaining gaps>`
```

Do not copy private prompts, tokens, credentials, customer data, or unverifiable
claims into an entry. A green check is evidence only for the check's actual
scope; it is not proof of a browser test, release publication, user pilot, or
production deployment.

## Real completed example

### 2026-09-15 — PR #167, isolate concurrent security scans

- Task type: CI reliability
- Issue: no separate issue recorded; the PR is
  https://github.com/nirzaf/stockpile/pull/167
- PR: https://github.com/nirzaf/stockpile/pull/167
- Branch: `codex/fix-security-workflow-concurrency`
- Tested commit: `22fbaed120a66f97a46871123bc7e4ba4ca8d7c5`
- Reviewed commit: `22fbaed120a66f97a46871123bc7e4ba4ca8d7c5`; native review
  summary: https://github.com/nirzaf/stockpile/pull/167#issuecomment-5679729359
- Merge commit: `6db17943cacb29e29b9396e011c643c61c5e71a1`
- Commands actually run: GitHub Actions workflow checks; local command
  transcript not recorded in this entry.
- Test results: Build & Test, CodeQL, container vulnerability scan, Trivy,
  and GitGuardian completed successfully. Cubic completed with a neutral
  result. Source: the PR check run at the link above.
- Failures encountered: `none recorded`.
- Fixes applied: security workflow concurrency was isolated by ref so unrelated
  runs do not cancel one another.
- Native Codex review: completed on the exact commit before merge;
  summary: https://github.com/nirzaf/stockpile/pull/167#issuecomment-5679729359
- Other review findings: no unresolved finding is recorded in the merged PR.
- Human disposition: merge completed normally on 2026-09-15; separate owner-only
  publication/deployment outcomes are not inferred.
- Usage/cost: `not recorded`; this native review must not be relabeled as
  project API-credit consumption.
- Human review time: `not recorded`.
- Limitations and owner follow-up: this example proves a CI maintenance merge,
  not application correctness, adoption, or production deployment.

This example is intentionally narrow and links to a real merged PR. It does not
backfill measurements that were not captured at the time.
