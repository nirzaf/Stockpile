#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RESOLVER="$ROOT_DIR/scripts/resolve-release-candidate.sh"
DOCKER_WORKFLOW="$ROOT_DIR/.github/workflows/docker.yml"
RELEASE_WORKFLOW="$ROOT_DIR/.github/workflows/release.yml"

fail() {
  echo "release workflow harness failed: $*" >&2
  exit 1
}

bash -n "$RESOLVER" || fail 'resolver has invalid Bash syntax'
bash -n "${BASH_SOURCE[0]}" || fail 'harness has invalid Bash syntax'

for file in "$DOCKER_WORKFLOW" "$RELEASE_WORKFLOW"; do
  [[ -f "$file" ]] || fail "missing workflow: $file"
  grep -Fq 'ref: ${{ github.sha }}' "$file" || fail "${file##*/} does not pin checkout to github.sha"
  grep -Fq 'scripts/resolve-release-candidate.sh' "$file" || fail "${file##*/} does not use the shared resolver"
  grep -Fq 'inputs.dry_run' "$file" || fail "${file##*/} has no dry-run gate"
  ! grep -Fq 'github.event.inputs' "$file" || fail "${file##*/} interpolates legacy event inputs"
done

grep -Fq 'mode docker' "$DOCKER_WORKFLOW" || fail 'Docker workflow does not select docker validation mode'
grep -Fq 'mode release' "$RELEASE_WORKFLOW" || fail 'Release workflow does not select release validation mode'
grep -Fq 'sha-${{ steps.candidate.outputs.candidate_sha }}' "$DOCKER_WORKFLOW" \
  || fail 'Docker workflow does not publish the immutable full-SHA image tag'
grep -Fq 'sha-${CANDIDATE_SHA}' "$RELEASE_WORKFLOW" \
  || fail 'Release workflow does not verify the immutable full-SHA image tag'

TEST_DIR="$(mktemp -d)"
trap 'rm -rf "$TEST_DIR"' EXIT
git init --bare "$TEST_DIR/remote.git" >/dev/null
git init "$TEST_DIR/source" >/dev/null
git -C "$TEST_DIR/source" config user.email harness@example.invalid
git -C "$TEST_DIR/source" config user.name harness
printf 'candidate\n' > "$TEST_DIR/source/file.txt"
git -C "$TEST_DIR/source" add file.txt
git -C "$TEST_DIR/source" commit -m candidate >/dev/null
git -C "$TEST_DIR/source" branch -M master
git -C "$TEST_DIR/source" remote add origin "$TEST_DIR/remote.git"
git -C "$TEST_DIR/source" push origin master >/dev/null
git -C "$TEST_DIR/source" tag v1.2.3
git -C "$TEST_DIR/source" push origin v1.2.3 >/dev/null
git -C "$TEST_DIR/source" tag -a v1.2.4 -m annotated-candidate
git -C "$TEST_DIR/source" push origin v1.2.4 >/dev/null

candidate_sha="$(git -C "$TEST_DIR/source" rev-parse HEAD)"
(
  cd "$TEST_DIR/source"
  "$RESOLVER" --remote origin --ref master --mode docker --event-name push --event-sha "$candidate_sha" >/dev/null
  "$RESOLVER" --remote origin --ref v1.2.3 --mode release >/dev/null
  "$RESOLVER" --remote origin --ref v1.2.4 --mode release >/dev/null
)

if (
  cd "$TEST_DIR/source"
  "$RESOLVER" --remote origin --ref 'master;touch-pwned' --mode docker >/dev/null 2>&1
); then
  fail 'malformed ref was accepted'
fi

if (
  cd "$TEST_DIR/source"
  "$RESOLVER" --remote origin --ref v1.2.3 --mode release --event-name push --event-sha \
    0000000000000000000000000000000000000000 >/dev/null 2>&1
); then
  fail 'stale push SHA was accepted'
fi

if (
  cd "$TEST_DIR/source"
  "$RESOLVER" --remote origin --ref master --mode release >/dev/null 2>&1
); then
  fail 'branch was accepted as a release candidate'
fi

git -C "$TEST_DIR/source" branch v1.2.3
git -C "$TEST_DIR/source" push origin refs/heads/v1.2.3:refs/heads/v1.2.3 >/dev/null
if (
  cd "$TEST_DIR/source"
  "$RESOLVER" --remote origin --ref v1.2.3 --mode docker >/dev/null 2>&1
); then
  fail 'ambiguous branch/tag ref was accepted'
fi

echo 'release workflow harness passed: structure, valid refs, malformed refs, stale SHAs, and release kind checks'
