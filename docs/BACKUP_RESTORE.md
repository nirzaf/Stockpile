# Backup and restore runbook

This runbook describes a disposable PostgreSQL recovery check for Merconiq. It
does not authorize a production restore. Always replace the example container
and volume names with an explicitly disposable target before running a destructive
command.

## What must be protected

- PostgreSQL data: items, locations, stock balances, transactions, purchase
  orders, users, tenants, and webhook/idempotency records.
- The data-protection key directory. In Compose this is the `dataprotection`
  volume mounted at `/app/data/keys`; losing it invalidates protected values and
  can invalidate existing authentication cookies or other protected tokens.
- The deployment configuration and secret references. Back up the secret store
  according to its provider's policy; never put passwords, JWT secrets, or key
  files in a Git commit or a support ticket.

## Create a logical backup

Run from the repository root with the target database stopped only if the
operator's database policy requires it. `pg_dump` makes a consistent logical
snapshot without copying credentials into the dump command's process list:

```bash
export PGHOST=localhost
export PGPORT="${DB_PORT:-5432}"
export PGDATABASE="${DB_NAME:-InventoryDB}"
export PGUSER="${DB_USER:-postgres}"
export PGPASSFILE="$(mktemp)"
chmod 600 "$PGPASSFILE"
printf '%s:%s:%s:%s:%s\n' "$PGHOST" "$PGPORT" "$PGDATABASE" "$PGUSER" "$DB_PASSWORD" > "$PGPASSFILE"
BACKUP_DIR="$(mktemp -d)"
pg_dump --format=custom --file="$BACKUP_DIR/merconiq.dump" "$PGDATABASE"
pg_restore --list "$BACKUP_DIR/merconiq.dump" > "$BACKUP_DIR/manifest.txt"
```

Record the database commit/schema version, UTC timestamp, database name, dump
format, and file checksum. Remove the temporary password file after the check:

```bash
sha256sum "$BACKUP_DIR/merconiq.dump"
rm -f "$PGPASSFILE"
```

## Restore into a different disposable database

Create a fresh database with a name that cannot be mistaken for the live target.
Do not use `--clean` or `--create` against a shared or production database:

```bash
export PGDATABASE=merconiq_restore_check
createdb "$PGDATABASE"
pg_restore --exit-on-error --no-owner --dbname="$PGDATABASE" "$BACKUP_DIR/merconiq.dump"
```

Start a Merconiq instance configured to use this database, apply no destructive
cleanup to the source database, and verify representative data through a fresh
application scope or the authenticated API:

- one item and its tenant;
- one location and the item/location balance;
- receive, transfer, and sale transaction rows, including quantity, lot, and
  source/destination locations;
- purchase-order and webhook/idempotency records where present; and
- a second tenant's records remain absent from the first tenant's queries.

The restored application must be able to authenticate only after the documented
data-protection keys and administrator configuration are supplied. A restored
database alone is not evidence that old cookies or sessions remain valid.

## Cleanup and verification record

After all assertions are captured, remove only the named disposable database and
temporary files:

```bash
dropdb --if-exists merconiq_restore_check
rm -rf "$BACKUP_DIR"
```

Record the exact commands, schema/data assertions, checksum, elapsed time if
measured, and any failure. This repository does not claim a recovery time or a
successful backup/restore verification until those fields contain real operator
evidence. In environments without PostgreSQL client tools and an isolated target,
leave the verification status as `not run` rather than substituting a health check.
