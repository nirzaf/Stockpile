#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: wait-for-validated-sha.sh --repository OWNER/REPOSITORY --sha FULL_SHA" >&2
}

die() {
  echo "candidate validation gate failed: $*" >&2
  exit 1
}

REPOSITORY="${GITHUB_REPOSITORY:-}"
SHA=""
MAX_ATTEMPTS="${VALIDATION_MAX_ATTEMPTS:-60}"
WAIT_SECONDS="${VALIDATION_WAIT_SECONDS:-10}"

while (($# > 0)); do
  case "$1" in
    --repository)
      (($# >= 2)) || { usage; exit 2; }
      REPOSITORY="$2"
      shift 2
      ;;
    --sha)
      (($# >= 2)) || { usage; exit 2; }
      SHA="$2"
      shift 2
      ;;
    *)
      usage
      exit 2
      ;;
  esac
done

[[ "$REPOSITORY" =~ ^[^/]+/[^/]+$ ]] || die 'repository must be OWNER/REPOSITORY'
[[ "$SHA" =~ ^[0-9a-f]{40}$ ]] || die 'sha must be a full lowercase commit SHA'
[[ "$MAX_ATTEMPTS" =~ ^[1-9][0-9]*$ && "$WAIT_SECONDS" =~ ^[0-9]+$ ]] || die 'wait settings must be numeric'
command -v gh >/dev/null 2>&1 || die 'gh CLI is required'

for ((attempt = 1; attempt <= MAX_ATTEMPTS; attempt++)); do
  all_successful=true
  # Use workflow filenames rather than display names. GitHub may expose a
  # workflow's configured name as its path (for example, security.yml) when
  # the workflow was registered or updated, so display-name lookup is brittle.
  for workflow in ci.yml security.yml; do
    runs="$(gh run list --repo "$REPOSITORY" --workflow "$workflow" --commit "$SHA" \
      --limit 20 --json status,conclusion 2>/dev/null)" \
      || die "could not inspect $workflow runs for the candidate"

    if jq -e 'any(.[]; .status == "completed" and .conclusion == "success")' <<<"$runs" >/dev/null; then
      continue
    fi

    if jq -e 'any(.[]; .status == "completed" and (.conclusion == "failure" or .conclusion == "cancelled" or .conclusion == "timed_out" or .conclusion == "action_required"))' <<<"$runs" >/dev/null; then
      die "$workflow did not pass for $SHA"
    fi

    all_successful=false
  done

  if [[ "$all_successful" == true ]]; then
    echo "validated CI and Security for $SHA"
    exit 0
  fi

  if ((attempt < MAX_ATTEMPTS)); then
    sleep "$WAIT_SECONDS"
  fi
done

die "CI and Security did not both complete successfully for $SHA before the validation timeout"
