#!/usr/bin/env bash
# synthetic-history.sh
#
# Build a simulated, multi-year Git commit history for the files in the
# current working directory, on a new ORPHAN BRANCH in the same repo.
# The existing history (and any other branch) is NEVER mutated.
#
#   1. Backs up the working directory (sans .git / .kilo) to /tmp/project_backup.
#   2. Verifies the repo has no uncommitted changes.
#   3. Creates an orphan branch (default: synthetic-history) in this repo.
#   4. Iterates through ~28 logical chunks (architectural layers / features).
#   5. For each chunk, stages the files, then commits with randomized
#      GIT_AUTHOR_DATE / GIT_COMMITTER_DATE spread across the last $DAYS_BACK days
#      (default: 548 days = 18 months, well over the requested 1 year).
#   6. Switches back to the original branch.
#   7. Prints a summary. NO `git push` is ever executed.
#
# Usage:
#   ./synthetic-history.sh                          # default: 18 months, main branch
#   SEED=42 ./synthetic-history.sh                  # reproducible date distribution
#   DAYS_BACK=730 ./synthetic-history.sh            # 2-year span
#   BRANCH_NAME=demo-history ./synthetic-history.sh # custom branch name
#
# To inspect afterwards:
#   git log --oneline synthetic-history
#   git checkout synthetic-history                  # actually work on the synthetic branch
#   git checkout master                             # return to your real work
#
# To delete the synthetic branch (and revert to the original):
#   git checkout master
#   git branch -D synthetic-history
#
# Nuclear restore from backup (also nukes .git and re-seeds from the backup):
#   rm -rf .git
#   cp -a /tmp/project_backup/. .
#
# Compatibility: bash 3.2+ (default on macOS), GNU date, optional rsync.

set -euo pipefail

# ============================================================
# Configuration
# ============================================================
BACKUP_DIR="/tmp/project_backup"
BRANCH_NAME="${BRANCH_NAME:-synthetic-history}"
DAYS_BACK="${DAYS_BACK:-548}"   # 18 months — comfortably more than 1 year
SEED="${SEED:-$RANDOM}"

# ============================================================
# Utility functions
# ============================================================
log()  { printf '\033[1;36m[synthetic-history]\033[0m %s\n' "$*" >&2; }
warn() { printf '\033[1;33m[synthetic-history]\033[0m %s\n' "$*" >&2; }
err()  { printf '\033[1;31m[synthetic-history]\033[0m %s\n' "$*" >&2; exit 1; }
have() { command -v "$1" >/dev/null 2>&1; }

# Generate a "YYYY-MM-DD HH:MM:SS ±HH:MM" string from an epoch.
make_git_date() {
  local epoch=$1
  local tz_hh=$(( (epoch / 3600) % 24 - 12 ))
  local tz_mm=$(( (epoch / 900) % 4 * 15 ))
  local tz
  tz=$(printf '%+03d:%02d' "$tz_hh" "$tz_mm")
  date -u -r "$epoch" "+%Y-%m-%d %H:%M:%S ${tz}"
}

# Random epoch in [lo, hi).
random_epoch_in() {
  local lo=$1 hi=$2 span=$(( hi - lo ))
  echo $(( lo + (RANDOM * RANDOM) % span ))
}

# ============================================================
# Step 1: Back up the current working directory
# ============================================================
log "=== Step 1: Back up current directory ==="
if [[ -d "$BACKUP_DIR" ]]; then
  warn "Removing existing backup at $BACKUP_DIR"
  rm -rf "$BACKUP_DIR"
fi
mkdir -p "$BACKUP_DIR"

if have rsync; then
  rsync -a \
    --exclude="$BACKUP_DIR/" \
    --exclude='.git/' \
    --exclude='.kilo/' \
    ./ "$BACKUP_DIR/" >/dev/null
else
  cp -a . "$BACKUP_DIR/"
  rm -rf "$BACKUP_DIR/.git" "$BACKUP_DIR/.kilo"
fi
[[ -n "$(ls -A "$BACKUP_DIR" 2>/dev/null)" ]] || err "Backup is empty — aborting"
log "✓ Backup saved to $BACKUP_DIR ($(du -sh "$BACKUP_DIR" | awk '{print $1}'))"

# ============================================================
# Step 2: Verify git and capture original state
# ============================================================
log "=== Step 2: Verify git ==="
[[ -d .git ]] || err "Not a git repository ($(pwd))"

if ! git diff --quiet HEAD 2>/dev/null; then
  err "Working tree has uncommitted changes. Commit, stash, or discard them first."
fi

ORIGINAL_BRANCH=$(git symbolic-ref --short -q HEAD 2>/dev/null \
                  || git rev-parse --short HEAD)
log "Original branch: $ORIGINAL_BRANCH"

GIT_AUTHOR_NAME_VAL="$(git config user.name  || true)"
GIT_AUTHOR_EMAIL_VAL="$(git config user.email || true)"
[[ -n "$GIT_AUTHOR_NAME_VAL" && -n "$GIT_AUTHOR_EMAIL_VAL" ]] \
  || warn "No user.name/user.email configured — using fallback for synthetic commits"

# ============================================================
# Step 3: Create orphan branch in the same repo
# ============================================================
log "=== Step 3: Create orphan branch '$BRANCH_NAME' in this repo ==="

# Remove the branch if it exists from a prior run
if git show-ref --verify --quiet "refs/heads/$BRANCH_NAME"; then
  warn "Branch '$BRANCH_NAME' already exists — deleting and recreating"
  # Make sure we're not on the branch before deleting
  if [[ "$(git symbolic-ref --short -q HEAD 2>/dev/null)" == "$BRANCH_NAME" ]]; then
    git checkout "$ORIGINAL_BRANCH" >/dev/null 2>&1 || true
  fi
  git branch -D "$BRANCH_NAME" 2>/dev/null || true
fi

# Make sure we're on a real branch (not detached) before creating the orphan
if ! git symbolic-ref --short -q HEAD >/dev/null 2>&1; then
  warn "HEAD is detached — checking out $ORIGINAL_BRANCH first"
  git checkout "$ORIGINAL_BRANCH" >/dev/null 2>&1
fi

# Create the orphan branch. The working tree is preserved; the index keeps
# whatever was staged on the old branch, which we clear in the next step.
git checkout --orphan "$BRANCH_NAME"

# Ensure the index is completely empty (working tree files are untouched).
# `git rm -rf --cached .` un-stages everything; `git read-tree --empty` then
# collapses the index so the next `git add` starts from a clean slate.
git rm -rf --cached . >/dev/null 2>&1 || true
git read-tree --empty 2>/dev/null || true

[[ -z "$(git status --porcelain 2>/dev/null)" ]] \
  || warn "Index has staged entries after cleanup; continuing anyway"

log "✓ Orphan branch '$BRANCH_NAME' created (working tree preserved, index empty)"

# ============================================================
# Step 4: Define logical chunks
# ============================================================
# Format per line:  "<commit-message>|<space-separated file/dir list>"
# Order in this heredoc = order in which commits are created
# (oldest → newest).
# ============================================================
read -r -d '' CHUNKS_DATA <<'CHUNKSEOF' || true
chore: bootstrap project config|.editorconfig .gitattributes .gitignore .dockerignore global.json .env.example
chore: add solution and project files|InventoryManagementSystem.sln InventoryManagementSystem.Core/InventoryManagementSystem.Core.csproj InventoryManagementSystem.Infrastructure/InventoryManagementSystem.Infrastructure.csproj InventoryManagementSystem.Web/InventoryManagementSystem.Web.csproj InventoryManagementSystem.Tests/InventoryManagementSystem.Tests.csproj
chore: add license|LICENSE
feat: add domain entities|InventoryManagementSystem.Core/Entities
feat: add domain interfaces|InventoryManagementSystem.Core/Interfaces
feat: add core services|InventoryManagementSystem.Core/Services
feat: add CQRS handlers and features|InventoryManagementSystem.Core/Features
feat: add domain models and DTOs|InventoryManagementSystem.Core/Models
feat: add entity framework data layer|InventoryManagementSystem.Infrastructure/Data
feat: add repositories and unit of work|InventoryManagementSystem.Infrastructure/Repositories
feat: bootstrap ASP.NET Core web app|InventoryManagementSystem.Web/Program.cs InventoryManagementSystem.Web/Properties InventoryManagementSystem.Web/appsettings.json InventoryManagementSystem.Web/appsettings.Development.json InventoryManagementSystem.Web/appsettings.Development.json.example InventoryManagementSystem.Web/appsettings.Testing.json
feat: add MVC controllers|InventoryManagementSystem.Web/Controllers
feat: add Blazor pages|InventoryManagementSystem.Web/Components/Pages
feat: add Blazor layouts and navigation|InventoryManagementSystem.Web/Components/Layout
chore: add web assets and static files|InventoryManagementSystem.Web/wwwroot
feat: add web middleware and error handling|InventoryManagementSystem.Web/Middleware InventoryManagementSystem.Web/Components/App.razor InventoryManagementSystem.Web/Components/Routes.razor InventoryManagementSystem.Web/Components/_Imports.razor InventoryManagementSystem.Web/GlobalExceptionHandler.cs InventoryManagementSystem.Web/Models
chore: add background services|InventoryManagementSystem.Web/BackgroundServices
test: add unit tests for core services|InventoryManagementSystem.Tests/Core
test: add integration tests|InventoryManagementSystem.Tests/Integration
test: add web controller tests|InventoryManagementSystem.Tests/Web
test: add test infrastructure|InventoryManagementSystem.Tests/Common
docs: add user guide|docs
ci: add GitHub Actions workflows|.github/workflows .github/dependabot.yml
ci: add issue and PR templates|.github/ISSUE_TEMPLATE .github/PULL_REQUEST_TEMPLATE.md
chore: add Docker build and compose|Dockerfile docker-compose.yml docker-compose.override.yml
chore: add development and deployment scripts|scripts
docs: add project README|README.md
docs: add contributing, security, and license docs|CONTRIBUTING.md CODE_OF_CONDUCT.md SECURITY.md MIGRATION_STATUS.md CHANGELOG.md
CHUNKSEOF

mapfile -t CHUNKS <<< "$CHUNKS_DATA"
[[ -z "${CHUNKS[-1]:-}" ]] && unset 'CHUNKS[-1]'
N=${#CHUNKS[@]}
log "Defined $N logical chunks"

# ============================================================
# Step 5: Distribute commits chronologically across the window
# ============================================================
log "=== Step 5: Generate $N chronological dates across last $DAYS_BACK days (≈$(( DAYS_BACK / 30 )) months) ==="
RANDOM="$SEED"

END_EPOCH=$(date +%s)
START_EPOCH=$(( END_EPOCH - DAYS_BACK * 86400 ))
WINDOW=$(( (END_EPOCH - START_EPOCH) / N ))

declare -a EPOCHS=()
for ((i = 0; i < N; i++)); do
  lo=$(( START_EPOCH + i * WINDOW ))
  hi=$(( lo + WINDOW ))
  EPOCHS[$i]=$(random_epoch_in "$lo" "$hi")
done

# Insertion sort for stability
for ((i = 1; i < N; i++)); do
  j=$i
  while (( j > 0 && EPOCHS[j] < EPOCHS[j-1] )); do
    tmp=${EPOCHS[j]}; EPOCHS[j]=${EPOCHS[j-1]}; EPOCHS[j-1]=$tmp
    j=$((j-1))
  done
done

# Sanity check
declare -A SEEN=()
for ((i = 0; i < N; i++)); do
  [[ ${EPOCHS[$i]} -ge $START_EPOCH && ${EPOCHS[$i]} -le $END_EPOCH ]] \
    || err "Epoch ${EPOCHS[$i]} out of range"
  [[ -z "${SEEN[${EPOCHS[$i]}]:-}" ]] \
    || err "Duplicate epoch ${EPOCHS[$i]} — try a different SEED"
  SEEN[${EPOCHS[$i]}]=1
done

# ============================================================
# Step 6: Commit each chunk in chronological order
# ============================================================
log "=== Step 6: Commit each chunk ==="
TOTAL_FILES=0
COMMITTED=0
SKIPPED=0
for ((i = 0; i < N; i++)); do
  chunk="${CHUNKS[$i]}"
  msg="${chunk%%|*}"
  file_list="${chunk#*|}"

  # Trim leading/trailing whitespace
  file_list="${file_list#"${file_list%%[![:space:]]*}"}"
  file_list="${file_list%"${file_list##*[![:space:]]}"}"

  # Build the file array, including only paths that exist
  files=()
  for f in $file_list; do
    if [[ -e "$f" ]]; then
      files+=("$f")
    fi
  done

  if [[ ${#files[@]} -eq 0 ]]; then
    warn "  ⏭  no matching files: $msg"
    SKIPPED=$((SKIPPED + 1))
    continue
  fi

  # Use -f to include files matched by .gitignore (appsettings.Development.json, etc.)
  git add -f -- "${files[@]}"

  git_date="$(make_git_date "${EPOCHS[$i]}")"
  pretty_date=$(date -u -r "${EPOCHS[$i]}" '+%Y-%m-%d %H:%M')

  GIT_AUTHOR_DATE="$git_date" \
  GIT_COMMITTER_DATE="$git_date" \
    git commit -q -m "$msg"

  TOTAL_FILES=$((TOTAL_FILES + ${#files[@]}))
  COMMITTED=$((COMMITTED + 1))
  log "  ✓ $pretty_date  $msg  (${#files[@]} paths)"
done

# ============================================================
# Step 7: Restore original branch and report
# ============================================================
log "=== Step 7: Restore original branch ==="
# Use -f: the synthetic branch is a SUBSET of the original (some files in
# the working tree are not in our chunks, so they show as untracked while
# the synthetic branch is checked out, and would block a clean checkout back).
# The final working tree will match the original branch — the synthetic
# branch's tracked files are a subset, so no data is lost.
if ! git checkout -f "$ORIGINAL_BRANCH" >/dev/null 2>&1; then
  warn "Could not switch back to '$ORIGINAL_BRANCH' — run 'git checkout -f $ORIGINAL_BRANCH' manually"
fi

COMMIT_COUNT=$(git rev-list --count "$BRANCH_NAME" 2>/dev/null || echo "?")
FIRST_DATE=$(git log --reverse --format='%ci' "$BRANCH_NAME" 2>/dev/null | head -1 || echo "?")
LAST_DATE=$(git log -1 --format='%ci' "$BRANCH_NAME" 2>/dev/null || echo "?")
FIRST_DATE_HUMAN=$(git log --reverse --format='%ad' --date=human "$BRANCH_NAME" 2>/dev/null | head -1 || echo "?")
LAST_DATE_HUMAN=$(git log -1 --format='%ad' --date=human "$BRANCH_NAME" 2>/dev/null || echo "?")

cat <<EOF

$(printf '\033[1;32m')================================================================
 ✓ Synthetic history built
================================================================$(printf '\033[0m')

  Backup location     : $BACKUP_DIR
  Original branch     : $ORIGINAL_BRANCH  (untouched)
  Synthetic branch    : $BRANCH_NAME
  Chunks committed    : $COMMITTED (skipped: $SKIPPED)
  Files committed     : $TOTAL_FILES paths
  Date range          : $LAST_DATE_HUMAN  →  $FIRST_DATE_HUMAN
  Date range (raw)    : $LAST_DATE  →  $FIRST_DATE
  Random seed         : $SEED
  Time span           : $DAYS_BACK days (≈$(( DAYS_BACK / 30 )) months)
  Remote touched      : NO   (no push was performed)

  Inspect with:
    git log --oneline $BRANCH_NAME
    git log --pretty=fuller $BRANCH_NAME
    git log --pretty=format:'%h %ad %s' --date=short $BRANCH_NAME

  Switch to the synthetic branch to look around:
    git checkout $BRANCH_NAME

  Return to your real work:
    git checkout $ORIGINAL_BRANCH

  Delete the synthetic branch (when you no longer need it):
    git checkout $ORIGINAL_BRANCH
    git branch -D $BRANCH_NAME

  Nuclear restore from backup:
    rm -rf .git
    cp -a $BACKUP_DIR/. .

EOF
