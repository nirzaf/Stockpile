# Security scanning

The Security workflow runs on pull requests targeting master, pushes to
master, and manual dispatches.

- CodeQL performs C# semantic static analysis using a manual Release build.
- Trivy scans the release Docker image built from the repository Dockerfile.
- Fixed HIGH and CRITICAL OS or library vulnerabilities fail the workflow.
- Unfixed findings are reported but do not fail the build because they require
  an upstream patch before the application can remediate them.

Trivy SARIF results are uploaded to GitHub code scanning. Any accepted exception
must be documented in the pull request with its affected package, reason, and a
follow-up owner or remediation date; broad suppressions are not permitted.
