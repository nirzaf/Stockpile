# Contributing to Merconiq

Thank you for your interest in contributing! This document outlines the process for contributing to this open-source project.

## Code of Conduct

Please read and follow our [Code of Conduct](CODE_OF_CONDUCT.md).

## How to Contribute

### Reporting Bugs

- Search existing [issues](https://github.com/nirzaf/merconiq/issues) to avoid duplicates
- Use the Bug Report template when creating a new issue
- Include clear steps to reproduce, expected vs actual behavior, and environment details

### Suggesting Features

- Use the Feature Request template
- Describe the problem the feature solves and how it benefits users
- Be open to discussion and alternative approaches

### Pull Requests

1. **Fork** the repository
2. **Create a branch** from the canonical repository's current `master` branch:
   ```bash
   git remote add upstream https://github.com/nirzaf/merconiq.git  # once, if upstream is not configured
   git fetch upstream master
   git switch -c codex/issue-<number>-<short-description> upstream/master
   ```
   If `upstream` already exists, keep its canonical URL and omit the `remote add` command. Contributors who work directly from the canonical repository may use `origin/master` only when `origin` is verified to be `https://github.com/nirzaf/merconiq.git`.
3. **Make changes** following our coding conventions
4. **Add tests** for new functionality
5. **Restore, build, and run all tests** to verify nothing is broken:
   ```bash
   dotnet restore
   dotnet build --no-restore --configuration Release
   dotnet test --no-build --configuration Release
   ```
   The repository pins the SDK to exactly **.NET 10.0.300** in `global.json` (`rollForward` is disabled); install that SDK before running these commands. A generic .NET 10 installation is not sufficient.
6. **Run the PostgreSQL phase** when a change involves relational constraints, transactions, concurrency, persistence boundaries, migrations, or API behavior that depends on PostgreSQL:
   ```bash
   RUN_POSTGRES_TESTS=true dotnet test tests/Merconiq.Tests/Merconiq.Tests.csproj \
     --no-build --configuration Release --filter "Category=PostgreSQL"
   ```
   The PostgreSQL phase uses Testcontainers to create a disposable `postgres:16-alpine` instance, so Docker must be installed and running in Linux-container mode; a locally installed PostgreSQL server alone is not sufficient for this command. InMemory tests are useful for fast unit coverage, but a skipped or InMemory-only test does not prove PostgreSQL behavior. Confirm that the PostgreSQL phase actually ran.
   The command above uses POSIX shell syntax. In PowerShell, set the environment variable for the command explicitly:
   ```powershell
   $previousRunPostgresTests = $env:RUN_POSTGRES_TESTS
   try {
     $env:RUN_POSTGRES_TESTS = "true"
     dotnet test tests/Merconiq.Tests/Merconiq.Tests.csproj --no-build --configuration Release --filter "Category=PostgreSQL"
   }
   finally {
     if ($null -eq $previousRunPostgresTests) { Remove-Item Env:RUN_POSTGRES_TESTS -ErrorAction SilentlyContinue }
     else { $env:RUN_POSTGRES_TESTS = $previousRunPostgresTests }
   }
   ```
7. **Inspect** `git diff --check`, the changed-file list, and the complete diff. Do not commit secrets, dumps, coverage output, or transient logs.
8. **Commit** with descriptive conventional-commit messages.
9. **Push only the issue branch** and open a Pull Request against `master`.

### Merconiq review and merge runbook

For an engineering change, use one issue → one fresh `codex/issue-<number>-<short-description>` branch → one focused PR. Keep the PR linked to the real issue and record the tested commit, commands, and limitations. Do not bundle unrelated work or commit directly to `master`.

After implementation and verification, mark the PR ready for review. Merconiq uses the existing native `chatgpt-codex-connector[bot]` integration. Request a review by commenting:

```text
@codex review

Please review the current PR head <FULL_HEAD_SHA> against master.
Check the linked issue's acceptance criteria, exact stock balances, tenant isolation,
transaction/outbox consistency, retry/idempotency safety, compatibility, and tests.
```

An eyes reaction or a posted review request only means that a review may be running. A completed native Codex summary tied to the current full head, with all findings inspected (and the integration's no-findings signal where applicable), is the review completion evidence. It is distinct from a GitHub `APPROVED` review, required human or CODEOWNERS approval, and the actual `MERGED` state.

For every material finding, inspect the full thread, reproduce or substantiate it, make the smallest valid fix, rerun relevant tests, push the new head, and request a fresh review for that new SHA. Record accepted and rejected dispositions with evidence; do not weaken tests or close a concern merely by resolving its thread.

Before merging, verify the current head and base, acceptance criteria, complete diff, required checks, completed current-head Codex review, resolved actionable findings, required approvals, and a usable merge state. Use the repository's normal merge strategy with GitHub's expected-head guard:

```bash
REVIEWED_HEAD="<FULL_REVIEWED_HEAD_SHA>"
REVIEWED_BASE_BRANCH="<REVIEWED_BASE_BRANCH>"
REVIEWED_BASE="<FULL_REVIEWED_BASE_SHA>"
CURRENT_HEAD="$(gh pr view <PR_NUMBER> --repo nirzaf/merconiq --json headRefOid --jq '.headRefOid')"
CURRENT_BASE_BRANCH="$(gh pr view <PR_NUMBER> --repo nirzaf/merconiq --json baseRefName --jq '.baseRefName')"
CURRENT_BASE="$(gh pr view <PR_NUMBER> --repo nirzaf/merconiq --json baseRefOid --jq '.baseRefOid')"
if test "$CURRENT_HEAD" != "$REVIEWED_HEAD"; then
  printf 'PR head changed: expected %s, found %s. Re-review before merging.\n' "$REVIEWED_HEAD" "$CURRENT_HEAD"
  exit 1
fi
if test "$CURRENT_BASE_BRANCH" != "$REVIEWED_BASE_BRANCH" || test "$CURRENT_BASE" != "$REVIEWED_BASE"; then
  printf 'PR base changed: expected %s at %s, found %s at %s. Re-review before merging.\n' "$REVIEWED_BASE_BRANCH" "$REVIEWED_BASE" "$CURRENT_BASE_BRANCH" "$CURRENT_BASE"
  exit 1
fi
if ! ACTIVE_RULESETS="$(gh api "repos/nirzaf/merconiq/rulesets" --paginate --slurp)"; then
  printf 'Could not inspect repository rulesets; do not merge until the target branch policy is known.\n'
  exit 1
fi
if ! DEFAULT_BRANCH="$(gh api "repos/nirzaf/merconiq" --jq '.default_branch')"; then
  printf 'Could not inspect the repository default branch; do not merge until the target branch policy is known.\n'
  exit 1
fi
if QUEUE_RULE_RESULT="$(printf '%s' "$ACTIVE_RULESETS" | jq -e --arg base_ref "refs/heads/$REVIEWED_BASE_BRANCH" --arg default_branch "$DEFAULT_BRANCH" '
  def selector_matches($selector; $ref; $default):
    if $selector == "~ALL" then true
    elif $selector == "~DEFAULT_BRANCH" then $ref == ("refs/heads/" + $default)
    elif ($selector | contains("*")) then
      ($selector
        | explode
        | map(. as $code
            | if $code == 42 then ".*"
              elif ([92, 94, 36, 46, 43, 63, 40, 41, 91, 93, 123, 125, 124] | index($code)) != null
                then "\\" + ([$code] | implode)
              else [$code] | implode
              end)
        | join("")
        | "^" + . + "$") as $pattern
      | $ref | test($pattern)
    else $selector == $ref
    end;
  def applies_to_base($ruleset; $ref; $default):
    ($ruleset.conditions.ref_name.include // []) as $includes
    | ($ruleset.conditions.ref_name.exclude // []) as $excludes
    | (($includes | length) == 0 or any($includes[]; selector_matches(.; $ref; $default)))
      and all($excludes[]?; selector_matches(.; $ref; $default) | not);
  flatten
  | any(.[]?;
      .enforcement == "active"
      and any(.rules[]?; .type == "merge_queue")
      and applies_to_base(.; $base_ref; $default_branch)
    )
'); then
  if test "$QUEUE_RULE_RESULT" = "true"; then
    printf 'The target branch requires a merge queue; stop before invoking gh pr merge.\n'
    exit 1
  fi
else
  QUEUE_RULE_STATUS=$?
  if test "$QUEUE_RULE_STATUS" -ne 1; then
    printf 'Could not parse repository rulesets; do not merge until the target branch policy is known.\n'
    exit 1
  fi
fi
gh pr merge <PR_NUMBER> --repo nirzaf/merconiq --squash --match-head-commit "$REVIEWED_HEAD"
```

The head and base comparisons are preflight checks, not an atomic expected-base guard: GitHub CLI only provides `--match-head-commit` for the head. They intentionally require a fresh review whenever `master` advances or the PR is retargeted, so use this runbook serially and do not merge concurrently or place the PR in a merge queue. After merging, record the actual merge commit and its first parent as evidence of the base that was merged. Never use an admin/bypass merge, disable checks, force-push, or lower an approval requirement. Preserve any applicable owner approval or confirmation-codeword rule; do not invent one.

After merging, verify that GitHub reports `MERGED`, record the merge commit, fetch `master`, and check its post-merge CI health before starting the next issue. A queued merge, green check, or review reaction is not proof of completion.

The normal workflows may have side effects: pushes to `master` publish the configured Docker image to GHCR, and changes under `docs/` can deploy GitHub Pages. Treat release publication, production deployment, user recruitment, and funding submission as separately authorized owner actions. Do not run `synthetic-history.sh`, rewrite history, or fabricate metrics.

### PR Guidelines

- Keep PRs focused on a single change
- Reference related issues (e.g., "Closes #123")
- Update documentation if applicable
- Ensure CI passes (build + tests)

## Newcomer path

Start with one small, reproducible change. The supported path is:

1. Read the [README](README.md), [Code of Conduct](CODE_OF_CONDUCT.md), and
   [Security policy](SECURITY.md).
2. Fork the repository, create a branch from the current `master`, and keep the
   change focused on one issue.
3. Make the smallest source or documentation change that addresses the issue.
4. Run the narrowest relevant test first, then the normal build/test commands
   above when the environment has the pinned SDK.
5. Open a PR against `master`, describe the reproduction and verification, and
   answer review comments. Do not include secrets, customer data, generated
   history, or unrelated formatting changes.

Useful first contributions are documentation corrections, focused regression tests,
accessibility/localization fixes, and reproducible bug fixes. Security boundaries,
authentication, migrations, and release publication need maintainer review and are
not automatically good-first issues.

### Starter-task candidates

These are bounded candidates, not manufactured issues or promises that a label has
already been applied. A maintainer should confirm the gap and create/link the issue
before a contributor starts:

| Candidate | Smallest useful change | First verification |
| --- | --- | --- |
| Documentation link check | Add or improve a check that catches broken relative Markdown links without crawling external sites. | Run it against `README.md` and `docs/`. |
| Webhook validation regression | Add a local, non-networking test for one documented unsafe URL case in `WebhookUrlValidator`. | Run the focused webhook validator tests. |
| Forecasting documentation | Correct a source mismatch in the forecasting guide while preserving the managed-moving-average default and SSA opt-in wording. | Compare the guide with `docs/FORECASTING_RUNTIME.md` and run Markdown checks. |

For clarification, comment on the issue with the exact command, source commit, and
the smallest unanswered question. Maintainers may be unavailable; this repository
does not promise a response-time SLA until the owner explicitly adopts one. If no
maintainer is available, leave the issue and PR state intact rather than opening
duplicate work or moving the project to Discussions.

### Review, AI assistance, and acknowledgement

The native `chatgpt-codex-connector[bot]` is an automated review signal. It is not a
human approval and does not replace a maintainer or CODEOWNERS decision. A completed
review must be tied to the current full PR head; any finding must be reproduced or
dispositioned with evidence before merge. AI-assisted contributors remain responsible
for the design, tests, licensing, and security of their submission.

Accepted work may be acknowledged only with the contributor's consent. Automated
agents, organization accounts, and external human contributors must remain distinct
when maintainers later report contribution metrics. No stars, forks, reciprocal
reviews, or artificial PR splitting are requested.

### Walkthrough evidence

The following is a maintainer simulation, not evidence of external participation:

```text
Fresh checkout -> read README and this file -> choose a bounded candidate
-> create codex/issue-<number>-<short-description>
-> run focused verification -> open one PR against master -> inspect CI and review
-> respond with evidence -> wait for normal maintainer merge.
```

An actual contributor walkthrough should be recorded only after a consenting person
completes it, with participant type, commit, commands, result, and friction points.
Do not turn this simulation into an adoption, responsiveness, or community claim.

## Development Setup

See the [README](README.md#getting-started) for setup instructions.

## Coding Conventions

- Follow standard C# naming conventions (PascalCase for types, camelCase for locals)
- Use async/await for I/O-bound operations
- Prefer constructor injection for dependencies
- Add XML doc comments on public APIs
- Keep methods small and focused
- Use `var` when the type is obvious, explicit types otherwise

## Commit Messages

Follow conventional commits format:

```
type(scope): description

- feat: new feature
- fix: bug fix
- docs: documentation
- refactor: code restructuring
- test: adding tests
- chore: maintenance tasks
```

## Project Structure

```
├── src/Merconiq.Core/         Domain layer
│   ├── Entities/                           Domain models
│   ├── Interfaces/                         Service contracts
│   └── Services/                           Business logic
├── src/Merconiq.Infrastructure/ Data access
│   ├── Data/                               DbContext, migrations, seed
│   └── Repositories/                       Repository implementations
├── src/Merconiq.Web/          ASP.NET Core MVC
│   ├── Controllers/                        MVC controllers
│   └── Views/                              Razor views
└── tests/Merconiq.Tests/        Test projects
```

## Questions?

Open a [Discussion](https://github.com/nirzaf/merconiq/discussions) or ask in an issue.
