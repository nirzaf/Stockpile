# Data Protection keys

Production instances use `DataProtection:KeyStoragePath` and persist ASP.NET Core
Data Protection keys to that directory. The container image defaults to
`/app/data/keys`, and `docker-compose.yml` mounts the named `dataprotection` volume
there. Keep that volume (or replace it with a shared durable filesystem mounted at
the same path) when replacing a container or running more than one replica.

Set `DataProtection__KeyStoragePath` to the mounted shared directory in another
deployment environment. The directory must be writable by the application identity
and should be protected with platform storage permissions and backups.

Development and Testing deliberately do not call `PersistKeysToFileSystem`; they
retain self-contained framework key repositories so tests and local runs do not depend
on production storage.
