#!/usr/bin/env bash
set -euo pipefail

# === Merconiq — Production Deployment ===
# Usage: ./scripts/deploy.sh [--build] [--migrate]
# Prerequisites: Docker + Docker Compose installed on target host

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"

cd "$PROJECT_DIR"

# Keep production selection explicit. docker-compose.override.yml is intentionally
# not supported because implicit Development settings are unsafe for deployment.
COMPOSE=(docker compose --file docker-compose.yml)

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

echo "=== Merconiq — Deployment ==="

# Load .env if present
if [ -f .env ]; then
    set -a
    source .env
    set +a
    echo "✅ Loaded .env configuration"
else
    echo "⚠️  No .env file found — required database and JWT settings must be exported"
fi

echo "🔍 Validating production Compose configuration..."
"$PROJECT_DIR/scripts/validate-compose.sh" production

# Pull latest images or build locally
if [ "$DO_BUILD" = true ]; then
    echo "🔨 Building Docker images..."
    "${COMPOSE[@]}" build --no-cache
else
    echo "📥 Pulling Docker images..."
    "${COMPOSE[@]}" pull 2>/dev/null || true
fi

# Start the database first when migrations were requested. Production migrations
# run in the isolated SDK migrator service, never in the application process.
if [ "$DO_MIGRATE" = true ]; then
    echo "🚀 Starting database..."
    "${COMPOSE[@]}" up -d --wait db

    echo "🗄️  Running database migrations..."
    "${COMPOSE[@]}" --profile migrations run --rm migrator

    echo "🚀 Starting application..."
    "${COMPOSE[@]}" up -d --wait app
else
    echo "🚀 Starting services..."
    "${COMPOSE[@]}" up -d --wait
fi

# Show status
echo ""
echo "📊 Service status:"
"${COMPOSE[@]}" ps

echo ""
echo "✅ Deployment complete!"
echo "   App:  http://localhost:${APP_PORT:-8080}"
echo "   Logs: docker compose --file docker-compose.yml logs -f app"
echo ""
echo "   Useful commands:"
echo "     docker compose --file docker-compose.yml logs -f app     # Follow app logs"
echo "     docker compose --file docker-compose.yml restart app     # Restart the app"
echo "     docker compose --file docker-compose.yml down            # Stop everything"
echo "     docker compose --file docker-compose.yml down -v         # Stop + remove volumes"
