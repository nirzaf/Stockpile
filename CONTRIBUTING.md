# Contributing to Stockpile

Thank you for your interest in contributing! This document outlines the process for contributing to this open-source project.

## Code of Conduct

Please read and follow our [Code of Conduct](CODE_OF_CONDUCT.md).

## How to Contribute

### Reporting Bugs

- Search existing [issues](https://github.com/nirzaf/Stockpile/issues) to avoid duplicates
- Use the Bug Report template when creating a new issue
- Include clear steps to reproduce, expected vs actual behavior, and environment details

### Suggesting Features

- Use the Feature Request template
- Describe the problem the feature solves and how it benefits users
- Be open to discussion and alternative approaches

### Pull Requests

1. **Fork** the repository
2. **Create a branch** from the current `master` branch:
   ```bash
   git fetch origin master
   git switch -c codex/issue-<number>-<short-description> origin/master
   ```
3. **Make changes** following our coding conventions
4. **Add tests** for new functionality
5. **Restore, build, and run all tests** to verify nothing is broken:
   ```bash
   dotnet restore
   dotnet build --no-restore --configuration Release
   dotnet test --no-build --configuration Release
   ```
6. **Run the PostgreSQL phase** when a change involves relational constraints, transactions, concurrency, persistence boundaries, migrations, or API behavior that depends on PostgreSQL:
   ```bash
   RUN_POSTGRES_TESTS=true dotnet test InventoryManagementSystem.Tests/InventoryManagementSystem.Tests.csproj \
     --no-build --configuration Release --filter "Category=PostgreSQL"
   ```
   InMemory tests are useful for fast unit coverage, but a skipped or InMemory-only test does not prove PostgreSQL behavior. Confirm that the PostgreSQL phase actually ran.
7. **Inspect** `git diff --check`, the changed-file list, and the complete diff. Do not commit secrets, dumps, coverage output, or transient logs.
8. **Commit** with descriptive conventional-commit messages.
9. **Push only the issue branch** and open a Pull Request against `master`.

### Stockpile review and merge runbook

For an engineering change, use one issue → one fresh `codex/issue-<number>-<short-description>` branch → one focused PR. Keep the PR linked to the real issue and record the tested commit, commands, and limitations. Do not bundle unrelated work or commit directly to `master`.

After implementation and verification, mark the PR ready for review. Stockpile uses the existing native `chatgpt-codex-connector[bot]` integration. Request a review by commenting:

```text
@codex review

Please review the current PR head <FULL_HEAD_SHA> against master.
Check the linked issue's acceptance criteria, exact stock balances, tenant isolation,
transaction/outbox consistency, retry/idempotency safety, compatibility, and tests.
```

An eyes reaction or a posted review request only means that a review may be running. A completed native Codex summary tied to the current full head, with all findings inspected (and the integration's no-findings signal where applicable), is the review completion evidence. It is distinct from a GitHub `APPROVED` review, required human or CODEOWNERS approval, and the actual `MERGED` state.

For every material finding, inspect the full thread, reproduce or substantiate it, make the smallest valid fix, rerun relevant tests, push the new head, and request a fresh review for that new SHA. Record accepted and rejected dispositions with evidence; do not weaken tests or close a concern merely by resolving its thread.

Before merging, verify the current head and base, acceptance criteria, complete diff, required checks, completed current-head Codex review, resolved actionable findings, required approvals, and a usable merge state. Use the repository's normal merge strategy with an expected-head guard. Never use an admin/bypass merge, disable checks, force-push, or lower an approval requirement. Preserve any applicable owner approval or confirmation-codeword rule; do not invent one.

After merging, verify that GitHub reports `MERGED`, record the merge commit, fetch `master`, and check its post-merge CI health before starting the next issue. A queued merge, green check, or review reaction is not proof of completion.

The normal workflows may have side effects: pushes to `master` publish the configured Docker image to GHCR, and changes under `docs/` can deploy GitHub Pages. Treat release publication, production deployment, user recruitment, and funding submission as separately authorized owner actions. Do not run `synthetic-history.sh`, rewrite history, or fabricate metrics.

### PR Guidelines

- Keep PRs focused on a single change
- Reference related issues (e.g., "Closes #123")
- Update documentation if applicable
- Ensure CI passes (build + tests)

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
├── InventoryManagementSystem.Core/         Domain layer
│   ├── Entities/                           Domain models
│   ├── Interfaces/                         Service contracts
│   └── Services/                           Business logic
├── InventoryManagementSystem.Infrastructure/ Data access
│   ├── Data/                               DbContext, migrations, seed
│   └── Repositories/                       Repository implementations
├── InventoryManagementSystem.Web/          ASP.NET Core MVC
│   ├── Controllers/                        MVC controllers
│   └── Views/                              Razor views
└── InventoryManagementSystem.Tests/        Test projects
```

## Questions?

Open a [Discussion](https://github.com/nirzaf/Stockpile/discussions) or ask in an issue.
