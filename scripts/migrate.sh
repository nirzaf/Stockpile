#!/usr/bin/env bash
set -euo pipefail

MIGRATION_NAME="${1:-InitialCreate}"
PROJECT_DIR="Merconiq.Infrastructure"
STARTUP_DIR="Merconiq.Web"

echo "=== Adding migration: $MIGRATION_NAME ==="
dotnet ef migrations add "$MIGRATION_NAME" \
  --project "$PROJECT_DIR" \
  --startup-project "$STARTUP_DIR"

echo "=== Applying migration ==="
dotnet ef database update \
  --project "$PROJECT_DIR" \
  --startup-project "$STARTUP_DIR"

echo "=== Migration complete ==="
