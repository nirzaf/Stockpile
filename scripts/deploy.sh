#!/usr/bin/env bash
set -euo pipefail

# === Inventory Management System — Production Deployment ===
# Usage: ./scripts/deploy.sh [--build] [--migrate]
# Prerequisites: Docker + Docker Compose installed on target host

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"

cd "$PROJECT_DIR"

# Parse flags
DO_BUILD=false
DO_MIGRATE=false
for arg in "$@"; do
    case "$arg" in
        --build) DO_BUILD=true ;;
        --migrate) DO_MIGRATE=true ;;
        *) echo "Unknown flag: $arg"; exit 1 ;;
    esac
done

echo "=== Inventory Management System — Deployment ==="

# Load .env if present
if [ -f .env ]; then
    set -a
    source .env
    set +a
    echo "✅ Loaded .env configuration"
else
    echo "⚠️  No .env file found — required database and JWT settings must be exported"
fi

# Pull latest images or build locally
if [ "$DO_BUILD" = true ]; then
    echo "🔨 Building Docker images..."
    docker compose build --no-cache
else
    echo "📥 Pulling Docker images..."
    docker compose pull 2>/dev/null || true
fi

# Start the database first when migrations were requested. Production migrations
# run in the isolated SDK migrator service, never in the application process.
if [ "$DO_MIGRATE" = true ]; then
    echo "🚀 Starting database..."
    docker compose up -d --wait db

    echo "🗄️  Running database migrations..."
    docker compose --profile migrations run --rm migrator

    echo "🚀 Starting application..."
    docker compose up -d --wait app
else
    echo "🚀 Starting services..."
    docker compose up -d --wait
fi

# Show status
echo ""
echo "📊 Service status:"
docker compose ps

echo ""
echo "✅ Deployment complete!"
echo "   App:  http://localhost:${APP_PORT:-8080}"
echo "   Logs: docker compose logs -f app"
echo ""
echo "   Useful commands:"
echo "     docker compose logs -f app     # Follow app logs"
echo "     docker compose restart app     # Restart the app"
echo "     docker compose down            # Stop everything"
echo "     docker compose down -v         # Stop + remove volumes"
