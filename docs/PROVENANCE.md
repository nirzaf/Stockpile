# Project provenance

Stockpile's supported history is the genuine commit and branch history in the
repository. The file `synthetic-history.sh` is retained as a clearly labeled,
non-supported utility for local history simulation experiments; it is not part
of the application build, deployment, release, or contribution workflow.

The script creates an orphan branch and generated timestamps/commits. It must
never be run to make project activity appear older or larger, and it must not be
used as evidence of real maintenance, adoption, longevity, contribution, or
impact. Do not backdate commits, rewrite shared history, or remove genuine
history to alter project optics.

## Metric treatment

Any report, grant draft, release note, or project summary must exclude generated
history from:

- repository age and continuity;
- maintainer/contributor counts and contribution frequency;
- issue, pull-request, release, and maintenance throughput;
- usage, adoption, reliability, coverage, and impact claims; and
- human review, API-credit, or engineering-time measurements.

When a generated artifact is discussed, label it `simulated`, identify its source
file, and state that it is not a production or project-history measurement.
Unknown genuine values remain `not recorded` or `unknown`; they are not filled
with simulated values.

## Supported workflow

Contributors should use the normal issue, branch, pull-request, review, test,
and merge process described in [CONTRIBUTING.md](../CONTRIBUTING.md). The
simulation script is intentionally absent from supported CI and deployment
commands. This document and the script's own warning are the complete
provenance disclosure; no history mutation is required to explain it.
