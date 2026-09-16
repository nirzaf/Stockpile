# Public GitHub evidence exporter

`scripts/github_evidence.py` produces a dated, read-only evidence report from
public GitHub REST metadata. It does not authenticate, write to GitHub, inspect
private data, run `synthetic-history.sh`, or infer adoption from stars or forks.

## Usage

From the repository root:

```bash
python scripts/github_evidence.py \
  --repo nirzaf/stockpile \
  --as-of 2026-09-16T00:00:00Z \
  --days 30 \
  --head-sha b1646978f51ac7493d70e1b701685d0e8fd7b7f2 \
  --format markdown \
  --output github-evidence.md
```

Omit `--head-sha` to resolve the public default-branch head first. Supplying a
full SHA is preferred when recording CI evidence because a branch can advance
between collection and review. The exporter can also emit JSON (the default)
for archival or further processing.

## Evidence rules

- The observation window is UTC and uses the half-open interval `[start, end)`.
  Issue and pull-request counts use `created_at`; commit counts use the commit
  author date, falling back to committer date.
- Every REST request, page URL, HTTP status, and error is recorded under
  `queries` so the report can be reproduced or audited.
- A successful empty endpoint is `{"status":"observed","value":0}`. A
  failed, malformed, or unavailable endpoint is
  `{"status":"unknown","value":null,...}`. Unknown is never rewritten as
  zero.
- Contributor records classified as `Bot` or with a `[bot]` login are excluded
  from human identity counts. Repeated records are collapsed by numeric GitHub
  ID, then case-insensitive login; no email address is used to identify anyone.
- CI evidence is limited to workflow runs whose `head_sha` exactly equals the
  selected full SHA. A green branch or a run for a different SHA is not an
  exact-SHA signal.
- Stars and forks are retained only as public visibility signals. They are not
  usage, adoption, active-user, customer, or impact measurements.

Local fixture coverage is in `scripts/test_github_evidence.py` and covers
pagination, valid empty data, API errors, bot exclusion, duplicate identities,
and exact-SHA filtering without contacting GitHub.
