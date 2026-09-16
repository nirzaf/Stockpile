#!/usr/bin/env bash
set -euo pipefail

# Validate the two supported Compose contracts without printing the resolved
# configuration. `docker compose config` includes passwords in its output, so
# every validation call uses --quiet or redirects the JSON to a private temp file.

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
cd "$PROJECT_DIR"

PRODUCTION_FILE="docker-compose.yml"
DEVELOPMENT_FILE="docker-compose.dev.yml"
MODE="${1:-all}"

if [ ! -f "$PRODUCTION_FILE" ]; then
    echo "Compose contract failed: missing $PRODUCTION_FILE" >&2
    exit 1
fi

if [ ! -f "$DEVELOPMENT_FILE" ]; then
    echo "Compose contract failed: missing $DEVELOPMENT_FILE" >&2
    exit 1
fi

if [ -e "docker-compose.override.yml" ]; then
    echo "Compose contract failed: docker-compose.override.yml must not exist" >&2
    exit 1
fi

if ! command -v docker >/dev/null 2>&1; then
    echo "Compose contract failed: Docker is required" >&2
    exit 1
fi

if ! command -v jq >/dev/null 2>&1; then
    echo "Compose contract failed: jq is required to inspect the rendered contract" >&2
    exit 1
fi

CONFIG_FILE="$(mktemp)"
trap 'rm -f "$CONFIG_FILE"' EXIT

validate_mode() {
    local mode="$1"
    local compose_args=(--file "$PRODUCTION_FILE")

    if [ "$mode" = "development" ]; then
        compose_args+=(--file "$DEVELOPMENT_FILE")
    fi

    if ! docker compose "${compose_args[@]}" config --quiet; then
        echo "Compose contract failed: $mode configuration is invalid or required values are missing" >&2
        exit 1
    fi

    # Keep rendered configuration out of logs because it contains credentials.
    docker compose "${compose_args[@]}" config --format json > "$CONFIG_FILE"

    if ! jq -e '
        (.services.db.environment.POSTGRES_PASSWORD | type == "string" and length > 0)
        and (.services.app.environment.JwtSettings__Secret | type == "string" and length > 0)
    ' "$CONFIG_FILE" >/dev/null; then
        echo "Compose contract failed: required database and JWT values must be non-empty" >&2
        exit 1
    fi

    if [ "$mode" = "production" ]; then
        if ! jq -e '.services.db.ports == null' "$CONFIG_FILE" >/dev/null; then
            echo "Compose contract failed: production must not publish PostgreSQL ports" >&2
            exit 1
        fi
    else
        if ! jq -e '(.services.db.ports | length) > 0' "$CONFIG_FILE" >/dev/null; then
            echo "Compose contract failed: development must publish the documented local PostgreSQL port" >&2
            exit 1
        fi
    fi
}

case "$MODE" in
    production|development)
        validate_mode "$MODE"
        ;;
    all)
        validate_mode production
        validate_mode development
        ;;
    *)
        echo "Usage: $0 [production|development|all]" >&2
        exit 2
        ;;
esac

echo "Compose contracts valid: $MODE configuration is safe and explicit."
