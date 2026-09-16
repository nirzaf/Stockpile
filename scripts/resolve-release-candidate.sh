#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat >&2 <<'EOF'
Usage: resolve-release-candidate.sh --ref REF --mode docker|release
       [--event-name NAME] [--event-sha SHA] [--remote REMOTE]
       [--github-output PATH]
EOF
}

die() {
  echo "release candidate validation failed: $*" >&2
  exit 1
}

REF_INPUT=''
MODE=''
EVENT_NAME=''
EVENT_SHA=''
REMOTE='origin'
GITHUB_OUTPUT_FILE=''

while (($# > 0)); do
  case "$1" in
    --ref)
      (($# >= 2)) || { usage; exit 2; }
      REF_INPUT="$2"
      shift 2
      ;;
    --mode)
      (($# >= 2)) || { usage; exit 2; }
      MODE="$2"
      shift 2
      ;;
    --event-name)
      (($# >= 2)) || { usage; exit 2; }
      EVENT_NAME="$2"
      shift 2
      ;;
    --event-sha)
      (($# >= 2)) || { usage; exit 2; }
      EVENT_SHA="$2"
      shift 2
      ;;
    --remote)
      (($# >= 2)) || { usage; exit 2; }
      REMOTE="$2"
      shift 2
      ;;
    --github-output)
      (($# >= 2)) || { usage; exit 2; }
      GITHUB_OUTPUT_FILE="$2"
      shift 2
      ;;
    *)
      usage
      exit 2
      ;;
  esac
done

[[ -n "$REF_INPUT" ]] || { usage; exit 2; }
[[ "$MODE" == 'docker' || "$MODE" == 'release' ]] || { usage; exit 2; }
[[ -n "$REMOTE" ]] || die 'remote must not be empty'

[[ "$REF_INPUT" != *$'\n'* && "$REF_INPUT" != *$'\r'* && "$REF_INPUT" != *' '* && "$REF_INPUT" != *$'\t'* ]] \
  || die 'ref contains whitespace'

if [[ "$REF_INPUT" == refs/heads/* || "$REF_INPUT" == refs/tags/* ]]; then
  CANDIDATE_REF="$REF_INPUT"
  if [[ "$REF_INPUT" == refs/heads/* ]]; then
    CANDIDATE_NAME="${REF_INPUT#refs/heads/}"
  else
    CANDIDATE_NAME="${REF_INPUT#refs/tags/}"
  fi
  [[ -n "$CANDIDATE_NAME" ]] || die 'ref name is empty'
else
  [[ "$REF_INPUT" != refs/* ]] || die 'only refs/heads/* and refs/tags/* are supported'
  git check-ref-format --allow-onelevel "$REF_INPUT" >/dev/null 2>&1 \
    || die 'ref is not a valid branch or tag name'

  branch_ref="refs/heads/$REF_INPUT"
  tag_ref="refs/tags/$REF_INPUT"
  branch_sha="$(git ls-remote --refs "$REMOTE" "$branch_ref" | awk 'NR == 1 { print $1 }')"
  tag_sha="$(git ls-remote --refs "$REMOTE" "$tag_ref" | awk 'NR == 1 { print $1 }')"
  [[ -n "$branch_sha" || -n "$tag_sha" ]] || die "ref does not exist on $REMOTE"
  [[ -z "$branch_sha" || -z "$tag_sha" ]] || die 'ref is ambiguous as both a branch and a tag'

  if [[ -n "$branch_sha" ]]; then
    CANDIDATE_REF="$branch_ref"
  else
    CANDIDATE_REF="$tag_ref"
  fi
  CANDIDATE_NAME="$REF_INPUT"
fi

git check-ref-format "$CANDIDATE_REF" >/dev/null 2>&1 \
  || die 'ref is not a valid fully-qualified Git ref'

remote_lines="$(git ls-remote "$REMOTE" "$CANDIDATE_REF" "$CANDIDATE_REF^{}")" \
  || die "could not read $CANDIDATE_REF from $REMOTE"
CANDIDATE_SHA="$(printf '%s\n' "$remote_lines" | awk -v ref="$CANDIDATE_REF" \
  '$2 == ref { direct = $1 } $2 == ref "^{}" { peeled = $1 } END { print peeled ? peeled : direct }')"
[[ "$CANDIDATE_SHA" =~ ^[0-9a-f]{40}$ ]] || die "ref does not resolve to one commit: $CANDIDATE_REF"

if [[ "$EVENT_NAME" == 'push' ]]; then
  [[ "$EVENT_SHA" =~ ^[0-9a-f]{40}$ ]] || die 'push event SHA is not a full commit SHA'
  [[ "$CANDIDATE_SHA" == "$EVENT_SHA" ]] || die \
    "push ref moved: event=$EVENT_SHA remote=$CANDIDATE_SHA"
fi

if [[ "$MODE" == 'release' ]]; then
  [[ "$CANDIDATE_REF" == refs/tags/* ]] || die 'a release must resolve an existing tag'
  [[ "$CANDIDATE_NAME" =~ ^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$ ]] \
    || die 'release tag must use vMAJOR.MINOR.PATCH with an optional prerelease suffix'
fi

CANDIDATE_KIND='branch'
if [[ "$CANDIDATE_REF" == refs/tags/* ]]; then
  CANDIDATE_KIND='tag'
fi
CANDIDATE_PRERELEASE='false'
if [[ "$CANDIDATE_KIND" == 'tag' && "$CANDIDATE_NAME" == *-* ]]; then
  CANDIDATE_PRERELEASE='true'
fi
CANDIDATE_SHORT_SHA="${CANDIDATE_SHA:0:12}"

if [[ -n "$GITHUB_OUTPUT_FILE" ]]; then
  {
    echo "candidate_ref=$CANDIDATE_REF"
    echo "candidate_name=$CANDIDATE_NAME"
    echo "candidate_kind=$CANDIDATE_KIND"
    echo "candidate_sha=$CANDIDATE_SHA"
    echo "candidate_short_sha=$CANDIDATE_SHORT_SHA"
    if [[ "$CANDIDATE_KIND" == 'tag' ]]; then
      echo "candidate_tag=$CANDIDATE_NAME"
    else
      echo 'candidate_tag='
    fi
    echo "candidate_prerelease=$CANDIDATE_PRERELEASE"
  } >> "$GITHUB_OUTPUT_FILE"
fi

echo "validated $CANDIDATE_REF at $CANDIDATE_SHA"
