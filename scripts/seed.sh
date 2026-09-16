#!/usr/bin/env bash
set -euo pipefail

echo "=== Running database seed ==="
export ASPNETCORE_ENVIRONMENT=Development
dotnet run --project Merconiq.Web

echo "=== Seed complete ==="
