# SQLite staging runtime contract

The explicit staging configuration selects a SQLite file in one persistent directory on the local host. API, bot and worker use the same file. Restrict directory access to the staging services, and keep database sidecar files alongside it. The schema operator initializes and versions the database before service startup and can seed a fresh database with synthetic fixtures.

Existing [admin API](../../../docs/ADMIN_API.md), Telegram callbacks and role entry points retain their documented behavior. Unavailable or uninitialized SQLite storage fails startup. Deployment rollback can restore the previous staging runtime and its configured database.
