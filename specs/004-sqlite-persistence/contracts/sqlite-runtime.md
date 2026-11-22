# SQLite staging runtime contract

The explicit staging configuration selects a SQLite file in one persistent directory on the local host. API, bot and worker use the same file. Restrict directory access to the staging services, and keep database sidecar files alongside it. The schema operator initializes and versions the database before service startup with an empty schema.

Existing [admin API](../../../docs/ADMIN_API.md), Telegram callbacks and role entry points retain their documented behavior. Unavailable or uninitialized SQLite storage fails startup. Before staging conversion, qualify only disposable empty SQLite files or an explicitly authorized isolated synthetic PostgreSQL snapshot with source/target row and schema verification; protected legacy and production data are excluded. The published SQLite database is authoritative for accepted staging writes.

Preserve the SQLite file and its WAL/SHM sidecars in the same persistent directory; validate schema identity, foreign keys, transaction integrity and startup permissions. Never copy only the database file while uncheckpointed WAL state may exist. Test recoverable, consistent backup/restore on an isolated copy, and reject a deployment that cannot safely read its persisted schema.

On failed first publication, the old PostgreSQL staging poller may resume only if no SQLite state was published. Once SQLite exists or accepts work, never automatically restart the old PostgreSQL writer: use the same accepted SQLite database with a previously qualified schema-compatible SQLite executable, or stop the staging roles and require explicit operator reconciliation. No rollback can discard accepted payments, stock, jobs or delivery obligations. Production and live provider activation remain independently gated.
